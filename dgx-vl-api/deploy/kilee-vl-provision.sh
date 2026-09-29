#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# kilee-vl-provision —— 把网关数据里的 devices[] 同步成受限隧道账号的 authorized_keys
#
# 以 root 由 kilee-vl-provision.timer 周期调用（幂等）：
#   - 确保系统用户 kilee-tunnel 存在（无 shell、无 home 登录）
#   - 依据 /var/lib/kilee-vl-admin/keys.json 的 devices[]（跳过 revoked）生成
#     ~kilee-tunnel/.ssh/authorized_keys，每把公钥锁定到分到的单一回环端口
#     （仅 permitlisten；禁止 -L 由 sshd Match 的 PermitOpen none 兜底）
# 内容无变化时不写入（避免无谓的 mtime/日志噪音）。
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

KEYS_FILE="${VL_KEYS_FILE:-/var/lib/kilee-vl-admin/keys.json}"
USER_NAME="${VL_TUNNEL_USER:-kilee-tunnel}"
HOME_DIR="${VL_TUNNEL_HOME:-/var/lib/kilee-tunnel}"
SSH_DIR="$HOME_DIR/.ssh"
AK="$SSH_DIR/authorized_keys"

[ "$(id -u)" = 0 ] || { echo "kilee-vl-provision 必须以 root 运行" >&2; exit 1; }

if ! id "$USER_NAME" >/dev/null 2>&1; then
  useradd -r -M -d "$HOME_DIR" -s /usr/sbin/nologin "$USER_NAME"
  echo "[provision] created system user $USER_NAME"
fi

install -d -m 0750 -o "$USER_NAME" -g "$USER_NAME" "$HOME_DIR"
install -d -m 0700 -o "$USER_NAME" -g "$USER_NAME" "$SSH_DIR"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

# 用 node 解析 keys.json（服务器上必有 node），产出 authorized_keys 内容
node -e '
const fs = require("fs");
const f = process.argv[1];
let s;
try { s = JSON.parse(fs.readFileSync(f, "utf8")); } catch (e) { process.exit(0); }
const devs = Array.isArray(s.devices) ? s.devices : [];
const out = [];
const seen = new Set();
for (const d of devs) {
  if (d.revokedAt) continue;
  if (!d.pubkey || !d.tunnelPort) continue;
  const parts = String(d.pubkey).trim().split(/\s+/);
  if (parts.length < 2) continue;
  const key = parts[0] + " " + parts[1];
  // 同一把公钥只允许一行：sshd 对重复公钥只认第一条匹配行，
  // 多余的行会让对应端口永远转发失败（设备一直离线）。保留第一条，跳过其余。
  if (seen.has(key)) {
    process.stderr.write("[provision] skip duplicate pubkey for device " + d.id + " (kept first line)\n");
    continue;
  }
  seen.add(key);
  // 注意：不要加 permitopen="none" —— sshd 会因无法解析该值而整行拒绝（静默失败）。
  // 禁止本地转发由 sshd Match 块的 PermitOpen none / AllowTcpForwarding remote 兜底。
  out.push(
    "restrict,port-forwarding,permitlisten=\"127.0.0.1:" + d.tunnelPort + "\" " +
    key + " kilee-vl:device=" + d.id
  );
}
process.stdout.write(out.length ? out.join("\n") + "\n" : "");
' "$KEYS_FILE" > "$TMP"

if ! cmp -s "$TMP" "$AK" 2>/dev/null; then
  install -m 0600 -o "$USER_NAME" -g "$USER_NAME" "$TMP" "$AK"
  echo "[provision] authorized_keys updated → $(grep -c . "$AK" || true) device(s)"
fi
