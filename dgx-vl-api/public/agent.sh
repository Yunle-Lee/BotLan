#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# kilee-vl agent —— 把本机 DGX Spark 上的 vLLM 通过反向隧道接入 kilee.cn
#
# 安装（在 DGX 上以 root 运行，一条命令）：
#   curl -fsSL https://kilee.cn/vl/agent.sh | sudo bash -s -- --pair <配对码> [--upstream 8001]
#
# 其它：
#   sudo bash agent.sh status      # 查看状态
#   sudo bash agent.sh uninstall   # 卸载（并到控制台删除设备）
#
# 原理：本机生成 ed25519 私钥（绝不外传），只用配对码注册【公钥】到服务器；
#       然后常驻一个 ssh -R 反向隧道，把服务器 127.0.0.1:<tunnelPort> 指到本机 vLLM。
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DIR=/etc/kilee-vl
SVC=kilee-vl-tunnel
KEY="$DIR/agent_key"
KNOWN="$DIR/known_hosts"
CFG="$DIR/config"
UNIT="/etc/systemd/system/${SVC}.service"
DEFAULT_SERVER="kilee.cn"

die() { echo "✗ $*" >&2; exit 1; }
info() { echo "• $*"; }
usage() {
  cat <<'USAGE'
用法:
  安装:  curl -fsSL https://kilee.cn/vl/agent.sh | sudo bash -s -- --pair <配对码> [--upstream 8001] [--server kilee.cn]
  状态:  sudo bash agent.sh status
  卸载:  sudo bash agent.sh uninstall
USAGE
}

# ── 子命令识别（首参不以 -- 开头才算子命令）──
SUBCMD=install
if [ $# -gt 0 ]; then
  case "$1" in
    status|uninstall|help|-h|--help) SUBCMD="$1"; shift ;;
  esac
fi

case "$SUBCMD" in
  help|-h|--help) usage; exit 0 ;;
  status)
    systemctl status "$SVC" --no-pager 2>/dev/null | head -n 12 || echo "服务未安装"
    echo; echo "配置："; cat "$CFG" 2>/dev/null || echo "(未配置)"
    exit 0 ;;
  uninstall)
    [ "$(id -u)" = 0 ] || die "请用 root 运行（sudo）"
    systemctl disable --now "$SVC" 2>/dev/null || true
    rm -f "$UNIT"; systemctl daemon-reload
    rm -rf "$DIR"
    info "已卸载本地隧道。请到控制台把该设备删除。"
    exit 0 ;;
esac

# ── 安装流程 ──
[ "$(id -u)" = 0 ] || die "请用 root 运行（例如 sudo bash agent.sh ...）"

PAIR=""; UPSTREAM=8001; SERVER="$DEFAULT_SERVER"
while [ $# -gt 0 ]; do
  case "$1" in
    --pair) PAIR="${2:-}"; shift 2 ;;
    --upstream) UPSTREAM="${2:-}"; shift 2 ;;
    --server) SERVER="${2:-}"; shift 2 ;;
    *) die "未知参数：$1" ;;
  esac
done
[ -n "$PAIR" ] || die "缺少 --pair <配对码>（在控制台点「添加设备」获取）"

# 依赖
if ! command -v ssh >/dev/null 2>&1 || ! command -v curl >/dev/null 2>&1; then
  info "安装依赖 openssh-client / curl …"
  if command -v apt-get >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get update -qq && apt-get install -y -qq openssh-client curl
  elif command -v dnf >/dev/null 2>&1; then dnf install -y -q openssh-clients curl
  elif command -v yum >/dev/null 2>&1; then yum install -y -q openssh-clients curl
  else die "请手动安装 openssh-client 与 curl"; fi
fi

install -d -m 0700 "$DIR"
if [ ! -f "$KEY" ]; then
  info "生成本机 ed25519 密钥（私钥只留本机）…"
  ssh-keygen -t ed25519 -N '' -C "kilee-vl" -f "$KEY" >/dev/null
fi
chmod 600 "$KEY" 2>/dev/null || true

PUB="$(cat "$KEY.pub")"
HOSTN="$(hostname | tr -cd 'A-Za-z0-9._-')"
[ -n "$HOSTN" ] || HOSTN="dgx-spark"

# 尽力探测本机 vLLM 的模型名（失败不影响）
MODELS_JSON='[]'
RAW="$(curl -sf -m 3 "http://127.0.0.1:${UPSTREAM}/v1/models" 2>/dev/null || true)"
if [ -n "$RAW" ]; then
  PARSED="$(printf '%s' "$RAW" | grep -o '"id"[[:space:]]*:[[:space:]]*"[^"]*"' | sed 's/.*"\([^"]*\)"$/"\1"/' | paste -sd, -)"
  [ -n "$PARSED" ] && MODELS_JSON="[${PARSED}]"
fi

info "向 $SERVER 注册设备（配对码 ${PAIR}）…"
BODY="$(printf '{"code":"%s","pubkey":"%s","upstreamPort":%s,"hostname":"%s","models":%s}' "$PAIR" "$PUB" "$UPSTREAM" "$HOSTN" "$MODELS_JSON")"
PAIR_TMP="$(mktemp)"
HTTP="$(curl -sS -m 20 -o "$PAIR_TMP" -w '%{http_code}' -X POST "https://${SERVER}/vl/api/devices/pair" -H 'content-type: application/json' -d "$BODY" 2>/dev/null || echo 000)"
RESP="$(cat "$PAIR_TMP" 2>/dev/null || true)"; rm -f "$PAIR_TMP"
if [ "$HTTP" != "200" ]; then
  # 把服务器给的明确原因透出来（例如：公钥已绑定到其他账号），避免只看到笼统的失败
  MSG="$(printf '%s' "$RESP" | sed -n 's/.*"error"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
  die "配对失败（HTTP ${HTTP}）${MSG:+：$MSG}"
fi

jget() { printf '%s' "$1" | grep -o "\"$2\"[[:space:]]*:[[:space:]]*\([^,}]*\)" | head -n1 | sed 's/.*:[[:space:]]*//; s/^"//; s/"$//'; }
DEV_ID="$(jget "$RESP" deviceId)"; [ -n "$DEV_ID" ] || die "服务器返回异常：$RESP"
TOKEN_PORT="$(jget "$RESP" tunnelPort)"
SERVER_USER="$(jget "$RESP" serverUser)"
SERVER_HOST="$(jget "$RESP" serverHost)"
SSH_PORT="$(jget "$RESP" sshPort)"
HOSTKEY_FP="$(jget "$RESP" hostKeyFp)"
HOSTKEY_LINE="$(printf '%s' "$RESP" | grep -o '"hostKeyLine"[[:space:]]*:[[:space:]]*"[^"]*"' | sed 's/^[^:]*:[[:space:]]*"//; s/"$//')"
[ -n "$RESP" ] || die "空响应"

info "分配隧道端口 ${TOKEN_PORT}，服务器 ${SERVER_USER}@${SERVER_HOST}:${SSH_PORT}"

# known_hosts：用配对返回的主机公钥固定（防中间人）
if [ -n "$HOSTKEY_LINE" ]; then
  printf '%b\n' "$HOSTKEY_LINE" > "$KNOWN"; chmod 600 "$KNOWN"
  info "已固定服务器主机公钥（${HOSTKEY_FP}）"
else
  : > "$KNOWN"; chmod 600 "$KNOWN"
  info "警告：服务器未提供主机公钥串，将使用 accept-new（首次信任）"
fi

SSH_BIN="$(command -v ssh)"
PORT_OPT=""; [ -n "$SSH_PORT" ] && [ "$SSH_PORT" != "22" ] && PORT_OPT="-p $SSH_PORT"
STATE_OPT="yes"; [ -z "$HOSTKEY_LINE" ] && STATE_OPT="accept-new"

cat > "$CFG" <<EOF
DEVICE_ID=$DEV_ID
SERVER_HOST=$SERVER_HOST
SERVER_USER=$SERVER_USER
SSH_PORT=$SSH_PORT
TUNNEL_PORT=$TOKEN_PORT
UPSTREAM_PORT=$UPSTREAM
EOF
chmod 600 "$CFG"

EXEC="$SSH_BIN -i $KEY -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=20 -o ServerAliveCountMax=3 -o IdentitiesOnly=yes -o UserKnownHostsFile=$KNOWN -o StrictHostKeyChecking=$STATE_OPT $PORT_OPT -N -R 127.0.0.1:$TOKEN_PORT:127.0.0.1:$UPSTREAM $SERVER_USER@$SERVER_HOST"

cat > "$UNIT" <<EOF
[Unit]
Description=Kilee VL reverse tunnel (device $DEV_ID)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$EXEC
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SVC" >/dev/null 2>&1 || true
systemctl restart "$SVC" >/dev/null 2>&1 || true

# 服务器的 provisioner 每 20s 把公钥同步到受限账号；首次连接在授权下发前会被拒，
# 而单元是 Restart=always，会自动重试。这里最多等 90 秒。
info "等待服务器下发隧道授权（首次约需 20 秒）…"
ok=0
for _i in $(seq 1 45); do
  sleep 2
  if systemctl is-active --quiet "$SVC"; then ok=1; break; fi
done

if [ "$ok" = 1 ]; then
  info "✓ 隧道已启动并设为开机自启"
else
  echo "✗ 隧道未能在 90 秒内建立，最近日志："; journalctl -u "$SVC" -n 15 --no-pager 2>/dev/null || true
  exit 1
fi

cat <<EOF

────────────────────────────────────────────
 接入完成 ✅   设备 ID: $DEV_ID
 隧道: 服务器 127.0.0.1:$TOKEN_PORT → 本机 127.0.0.1:$UPSTREAM

 下一步：回到控制台
   1) 「设备」里应显示为「在线」
   2) 创建 API Key（默认绑定这台设备）
   3) Base URL: https://$SERVER_HOST/api/vl
────────────────────────────────────────────
EOF
