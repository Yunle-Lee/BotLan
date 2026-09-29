# 多租户设计草案（Draft for review）

> **历史文档，已按本文落地（2026-09）。** 保留它作为演进记录；**现行实现以
> [`README.md`](README.md) 为准**。
>
> ⚠️ 一处勘误：§5.2 授权模板里的 `permitopen="none"` **不要照抄** —— sshd 解析不了该值，
> 会**整行静默拒绝**，表现为隧道永远 `Permission denied (publickey)`。禁止本地转发由
> `Match User kilee-tunnel` 里的 `PermitOpen none` 兜底即可。详见 README §6 与 §10.1。

> 状态：本文只做设计记录，不含任何对服务器的改动。
> 目标：让其他人也把自己的 DGX Spark 接到 kilee.cn，由网页为其创建 API key、提供统一 base URL。
> 现有单租户实现见 `README.md`、`server.js`。

---

## 1. 目标与非目标

**目标**
- 用户在网页**自助注册/登录** → 配对（pair）自己的 DGX Spark → 网页为其建 key。
- 对外**统一 base URL**：`https://kilee.cn/api/vl`（不变），**key 决定路由到哪台设备**。
- 租户可随时下线设备、吊销 key；管理方（你）可一键停用某租户。

**非目标（本期不做）**
- 计费/支付、套餐额度结算（只预留字段）。
- 让服务器主动连别人的 DGX（见 §2，永久排除）。
- 每个租户独立子域名，本期用统一路径。

---

## 2. 不可动摇的原则：永远 outbound

**DGX 主动向 kilee.cn 建反向隧道，服务器绝不主动连 DGX。**

理由：
1. DGX Spark 多在家庭 NAT 后，无法被主动拨入；即使对方在数据中心，也没有理由让你替他保管 SSH 凭据。
2. 让用户填写 SSH 密码/私钥 = 你替所有人保管机器钥匙，泄露由你背锅。方向必须反过来，这也是 ngrok / Tailscale / Cloudflare Tunnel 的共同做法。

结论：**配对 = DGX 主动外连 + 只注册公钥**，私钥永不离开对方机器。

## 3. 数据模型（在现有 keys.json 上扩展，version 1 → 2）

现状：`store = { version, keys[], playground }`，key 无归属、上游写死 `127.0.0.1:8801`。

目标结构（JSON 字段示意）：

```jsonc
{
  "version": 2,
  "accounts": [
    { "id":"acc_x", "username":"alice", "passwordHash":"scrypt$...",
      "sessionSecret":"...", "createdAt":0, "disabled":false, "role":"tenant" }
  ],
  "devices": [
    { "id":"dev_x", "accountId":"acc_x", "name":"alice-spark",
      "tunnelPort":8821, "upstreamPort":8001, "pubkeyFp":"SHA256:...",
      "models":["qwen3-vl"], "status":"online", "lastSeenAt":0,
      "createdAt":0, "revokedAt":null }
  ],
  "keys": [
    { "id":"...", "accountId":"acc_x", "deviceId":"dev_x",
      "label":"...", "key":"vl_...", "createdAt":0, "expiresAt":null,
      "revokedAt":null, "lastUsedAt":null,
      "requests":0,"ok":0,"errors":0,"promptTokens":0,"completionTokens":0,
      "quota":{ "maxRequestsPerDay":0, "maxTokensPerDay":0 } }
  ],
  "pairings": [
    { "code":"K7F2-9QXM", "accountId":"acc_x", "createdAt":0,
      "expiresAt":0, "usedAt":null, "requestedPort":8821 }
  ],
  "playground": {}
}
```

关键变化：
- 新增 `accounts`（自助登录）与 `devices`（每台 DGX = 一个上游）。
- `keys[].deviceId` 把 key 绑定到设备 —— **key 决定路由**，所以对外 base URL 不变（见 §6）。
- `devices[].tunnelPort` = 分配到的服务器回环端口；`quota` 中 `0` 表示不限。

**迁移（v1 → v2）**：现有那把 key 归入内置账号 `owner`，新建设备 `spark-d1b9`（`tunnelPort:8801`、`upstreamPort:8001`），把该 key 的 `deviceId` 指向它；旧的管理密码继续作为 `owner` 的密码。

## 4. 配对协议

目标：用户用"一条命令 + 一个码"完成注册，且服务器**不接触对方私钥**。

### 4.1 时序

1. 用户登录控制台 → "添加设备" → `POST /vl/api/devices/pair/start`
   → 服务器生成一次性配对码（8 位，`K7F2-9QXM` 风格；TTL 10 分钟；单次有效），返回
   `{ code, expiresAt, serverHost:"kilee.cn", sshPort:22 }`。
2. 用户在 DGX 上执行：
   ```bash
   curl -fsSL https://kilee.cn/vl/agent.sh | sudo bash -s -- --pair K7F2-9QXM --upstream 8001
   ```
3. `agent.sh` 依次：
   - 校验 root 与依赖（`openssh-client`、`curl`）；
   - 若无则生成 ed25519 密钥对 `/etc/kilee-vl/agent_key`（`0600`）；
   - `POST https://kilee.cn/vl/api/devices/pair`，body：
     `{ code, pubkey:"ssh-ed25519 AAAA... kilee-vl", upstreamPort:8001, hostname:"spark-xxxx", models:["qwen3-vl"] }`；
   - 服务器校验配对码 → 定位账号 → 分配端口 → 落公钥（§5）→ 返回
     `{ deviceId, serverUser, serverHost, tunnelPort, hostKeyFp:"SHA256:..." }`；
   - agent 把 `hostKeyFp` 写入 `known_hosts`（**首次即成信任锚，防中间人**）；
   - 生成并启用 `kilee-vl-tunnel.service`（§7），随后发送一次心跳。
4. 控制台刷新 → 设备显示"在线"，并回显其 `/v1/models` 的模型名。

### 4.2 配对码安全

- 熵：8 位取自 32 字符表 ≈ 40 bit；配合 **10 分钟 TTL + 单次使用 + 失败限流**足够。
- `POST /vl/api/devices/pair` 是**未鉴权端点**，必须以"配对码即凭据"处理：限流（如 5 次/分/IP）、
  仅接受 JSON、拒绝已用/过期码。
- 一个码只允许注册**一个**公钥；重复使用返回 `409`。

## 5. 服务器侧：受限隧道账号

### 5.1 账号模型（二选一）

- **方案 A（推荐，简单）**：所有租户共用一个系统用户 `kilee-tunnel`，隔离靠**每把公钥的 `permitlisten`**。
- **方案 B（隔离更彻底）**：每租户一个系统用户 `kvt-acc_x`，便于按用户切断单条隧道、日志可审计。

本期建议 A：隔离已被密钥选项强制，且免去大量 `useradd`。若日后需要"精确切断某一家"，再演进到 B。

### 5.2 authorized_keys 模板（关键安全点）

```
restrict,port-forwarding,permitlisten="127.0.0.1:8821",permitopen="none" ssh-ed25519 AAAA... kilee-vl:dev_x
```

逐项含义：
- `restrict`：关闭 PTY、agent/X11 转发、`~/.ssh/rc` 等一切；
- `port-forwarding`：在 restrict 基础上只放开端口转发；
- `permitlisten="127.0.0.1:8821"`：**只允许**把端口绑到服务器回环的这个端口（限制 `-R` 目标）；
- `permitopen="none"`：**禁止一切本地转发（`-L`）** —— 否则对方能拿你的服务器当跳板摸内网，这是头号漏洞。

### 5.3 sshd_config Match 兜底

```
Match User kilee-tunnel
    AllowTcpForwarding remote       # 只许 -R，堵死 -L
    PermitOpen none
    GatewayPorts no                 # 只绑回环
    PermitTTY no
    X11Forwarding no
    AllowAgentForwarding no
    ForceCommand /usr/sbin/nologin  # 万一试图开 shell 也直接拒
```

> `PermitListen` 在 Match 中受支持（已确认本机 sshd 版本）。端口限制放 authorized_keys（每密钥粒度），
> 策略放 Match（全局粒度），两者互补。端口从 **8820 起**分配，避开网关自身的 `8811` 与现有 `8801`。

改 sshd 前照例：**备份 → `sshd -t` → reload**。

## 6. 网关（server.js）改造

保持**对外接口不变**，内部从"单上游"变"按 key 找设备"：

1. `findKey()` 命中后取 `key.deviceId` → 查 `devices[deviceId].tunnelPort` → 转发到 `127.0.0.1:<tunnelPort>`。
   `forwardUpstream()` 现用全局 `CFG.upstreamPort`，改为**入参 `upstreamPort`**。
2. `checkUpstream()` 按 `deviceId` 缓存健康结果（`Map<deviceId,{at,data}>`），API 增加设备维度。
3. `/api/vl/*` 路由**无需加租户路径**：key 已决定设备，统一 base URL 天然成立。
4. 模型名：透传设备 `/v1/models` 的回显；控制台"接入信息"按设备展示各自模型名。
5. 用量：`recordKeyUsage` 不变，另在账户维度聚合（`sum by accountId`）。
6. 配额（预留）：`quota.maxRequestsPerDay / maxTokensPerDay`，在 `handleInference` 入口做滑动窗口计数，
   超限返回 `429 {error:{type:"rate_limit_error"}}`。
7. 账号鉴权：会话由"单密码"扩展为 `accounts`；`signSession` 带 `accountId`；`requireSession` 校验账号未禁用。
   owner（你）额外具备"管理所有账号/设备"的 admin 权限。

> 对现有单租户链路**向后兼容**：迁移后 owner 的 key 仍指向 8801，行为与今天完全一致。

## 7. DGX 端：agent.sh（连接器）

职责只四件，保持极小：

1. **装**：检测/安装 `openssh-client`、`curl`；建 `/etc/kilee-vl/`（0700）。
2. **配**：生成 ed25519 密钥（仅一次）；用配对码注册公钥，换回 `tunnelPort / serverHost / serverUser / hostKeyFp`。
3. **起**：写 `/etc/kilee-vl/config` 与 systemd 单元；`daemon-reload && enable --now`。
4. **报**：轻量心跳，`POST /vl/api/devices/heartbeat`（带设备令牌）上报在线状态与模型列表；
   收到 `revoked` 即自停并退出。

生成的单元（DGX 侧，参数为占位）：

```ini
[Unit]
Description=Kilee VL reverse tunnel
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/bin/ssh -i /etc/kilee-vl/agent_key \
  -o BatchMode=yes -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=20 -o ServerAliveCountMax=3 \
  -o UserKnownHostsFile=/etc/kilee-vl/known_hosts \
  -o StrictHostKeyChecking=yes \
  -N -R 127.0.0.1:${tunnelPort}:127.0.0.1:${upstreamPort} \
  ${serverUser}@${serverHost}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

子命令：`agent.sh status` / `agent.sh uninstall`（停单元、删 `/etc/kilee-vl`、通知服务器删设备）。

> 这条单元就是你现有 `dgx-vl-tunnel.service` 的参数化版本，逻辑一致，只是端口/用户/主机变变量。

## 8. 安全清单

- [ ] 第三方公钥一律走 §5.2 模板（`restrict` + `permitlisten` + `permitopen="none"`），**缺一项都不上**。
- [ ] 配对码：高熵、TTL、单次、限流（§4.2）。
- [ ] 服务器 host key 用配对返回的指纹固定（`StrictHostKeyChecking=yes`）。
- [ ] 私钥只存在对方 DGX（`/etc/kilee-vl/agent_key`），服务器只存公钥与指纹。
- [ ] 每 key 配额与限流；保留 nginx `vlapi` 限流，另在网关做**按账户/设备**的桶。
- [ ] 下线租户 = 禁用账号 → 删 authorized_keys 条目 → 切断该租户 sshd 会话 → 删设备。
- [ ] 审计：登录、配对、建/吊销 key、心跳异常全部落日志（现有 `console.log` 结构化即可）。
- [ ] 隐私：流量过境服务器，需在页面明确告知（图片/对话会被中转）；不落盘媒体正文。

## 9. 分阶段落地（每阶段可独立验证、可回滚）

- **P1（验证链路，最小）**：仍单管理员。手工加**第二台**设备 + 配对接口 + 多 upstream 路由。
  验收：另一台 DGX 用 `agent.sh` 接入，控制台用不同 key 分别调用，各自 200。
- **P2（自助）**：开放注册/登录、控制台"添加设备"页、按账号管理 key 与配额。
- **P3（运营）**：额度/计费、审计页、下线流程自动化。（本期不实现）

## 10. 待你拍板的点

1. **账号模型**：共用 `kilee-tunnel`（简单）还是每租户独立用户（易切断）？→ 建议先共用。
2. **准入**：开放注册，还是**邀请制**（由你发配对码）？涉及滥用与算力成本。
3. **配额默认值**：每 key 默认每天多少请求/token？超限是拒绝还是降级？
4. **隐私口径**：页面是否承诺"不落盘、不出售"？是否提供"各家自带域名直连"（流量不过境）？
5. **可用性**：对方 DGX 关机/切模型时网关返回什么（建议 `503 upstream_unavailable`，控制台显示离线）。

> **当前已上线默认（都可通过环境变量改，见下）**：
> 1. 账号模型 = 共用 `kilee-tunnel`，隔离靠每把公钥的 `permitlisten`；
> 2. 准入 = 暂时**开放注册**（`VL_ALLOW_REGISTER=1`，未设邀请码）；要转邀请制就设 `VL_INVITE_CODE=xxx`（设了即强制）；
> 3. 配额默认 = 不限（`maxRequestsPerDay=0` / `maxTokensPerDay=0`），key 级可单独设；超限返回 `429`；
> 4. 隐私 = 暂未在页面承诺"不落盘"，也未提供各家自带域名直连（流量仍过 kilee.cn）；
> 5. 设备离线时 key 请求返回 `403 device_unavailable`，控制台显示"离线"（不是 503，待统一口径）。

---

## 附：本文与现有实现的对应

| 本文 | 现有文件 / 值 |
|---|---|
| 网关改造 §6 | `server.js`（`CFG.upstreamPort=8801`、`findKey`、`forwardUpstream`） |
| 数据模型 §3 | `/var/lib/kilee-vl-admin/keys.json`（version 1） |
| SSH 隧道 §7 | `~/.config/systemd/user/dgx-vl-tunnel.service` |
| sshd 改动 §5 | `/etc/ssh/sshd_config`（备份 `/root/sshd_config.bak.*`） |
| 单租户现状 | `README.md` |

_（草案结束，待评审）_
