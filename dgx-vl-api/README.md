# DGX VL API —— 把本地 DGX 上的多模态模型变成公网 OpenAI 兼容 API

把 NVIDIA DGX Spark 上本地运行的 vLLM 模型（`Qwen3-VL-30B-A3B-Thinking`，BF16）
经跳板机 `kilee.cn` 暴露为**公网 OpenAI 兼容 API**，并配一个带 API key 管理、
用量统计与在线对话测试台的**网页控制台**。

支持**多租户**：多台 DGX、多个账号，对外共用同一个 Base URL，由 API key 决定路由到哪台设备。

| | |
|---|---|
| 控制台 | <https://kilee.cn/vl/> |
| Base URL | `https://kilee.cn/api/vl` |
| 模型名 | `qwen3-vl`（必须精确是这个值） |

---

## 1. 架构

```
浏览器 ──> https://kilee.cn/vl/         静态 SPA（nginx alias 直出，无后端渲染）
浏览器 ──> https://kilee.cn/vl/api/*    会话鉴权（账号密码 → HMAC 签名 cookie）
第三方 ──> https://kilee.cn/api/vl/*    API key 校验 + 用量统计 + 按 key 路由
                                    │
                        127.0.0.1:8811  kilee-vl-admin（网关 + 控制台后端，仅回环）
                                    │
                  127.0.0.1:8820…8899  每台设备独占一个回环端口（GatewayPorts no）
                                    │  SSH 反向隧道（DGX 主动外连；服务器绝不反连）
                                    ▼
                        各租户 DGX Spark 127.0.0.1:8001
                                    │  vLLM 容器 vllm-qwen3vl
                                    ▼
                       qwen3-vl（Qwen3-VL-30B-A3B-Thinking, BF16）
```

**不可动摇的原则：永远 outbound。** DGX 主动向 `kilee.cn` 建反向隧道，服务器从不主动连 DGX。
服务器只存**公钥**，私钥永远留在对方机器上；即便服务器被攻破也拿不到任何 DGX 的 shell。

外部只有两个入口：`/vl/`（控制台）与 `/api/vl/*`（推理）。隧道端口全部只监听 `127.0.0.1`，
云防火墙只放行 22 / 80 / 443。**dgx-bridge 的 `/agent`（内含 `run_shell` 等工具）不对外暴露。**

## 2. 目录结构

```
server.js                            网关 + 控制台后端（纯 Node 内置模块，零依赖）
init-admin.js                        一次性初始化：生成管理密码、迁移数据到 v2
public/index.html                    控制台前端（单文件 SPA）
public/agent.sh                      在 DGX 上跑的连接器：生成密钥 → 配对 → 装 systemd 隧道
deploy/kilee-vl-provision.sh         服务器侧 provisioner：把 devices[] 幂等同步成 authorized_keys
deploy/kilee-vl-provision.service    provisioner 的 oneshot 单元
deploy/kilee-vl-provision.timer      每 20s 触发一次 provisioner
deploy/sshd-match-kilee-tunnel.conf  sshd 的 Match 片段（隧道账号权限收敛）
kilee-vl-admin.service               kilee-vl-admin 的 systemd 单元
patch-nginx.py                       给已有 nginx 站点打补丁（插入三个 location）
DESIGN-multitenant.md                多租户设计草案（演进记录，含取舍理由）
```

服务器上的落地路径见 §8。

## 3. 控制台能干什么

| 区域 | 功能 |
|---|---|
| 概览 | 设备在线状态、key 统计、请求/token 汇总、Base URL 与 curl 示例一键复制 |
| 我的设备 | 「+ 添加设备」生成配对码与一条命令；添加后 **3 秒轮询**直到设备变「在线」；显示 `N / 上限 台设备`，并统计疑似僵尸设备数 |
| 僵尸设备清理 | 从未上线且创建 > 1h、或最后在线 > 24h 的设备会被标记「疑似僵尸」，可一键列出 + 确认 + 批量删除 |
| API Keys | 创建（可设有效期）、轮换、吊销、恢复、删除、清零统计；每把 key 显示请求数/失败数/输入输出 token/最后使用时间 |
| 每日配额 | 每把 key 可单独设「每日请求数上限」「每日 token 上限」，`0` = 不限；弹窗内直接显示今日已用 |
| 对话测试台 | 直接对话 + 上传/粘贴图片，支持流式；走同一条链路但不消耗 key 额度 |
| 账号 | 自助注册/登录、改密码（改完所有旧会话立即失效）；owner 可停用/启用/删除任意租户 |

控制台**每 15 秒自动刷新**一次（弹窗打开、正在提交、页面不可见时自动跳过；
页面重新可见时立刻刷新一次）。

## 4. 调用方式

```bash
KEY=vl_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

# 文本
curl https://kilee.cn/api/vl/chat/completions \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"qwen3-vl","messages":[{"role":"user","content":"你好"}],"max_tokens":1200}'

# 看图（OpenAI 多模态格式，data URL 传 base64）
python3 - <<'PY'
import base64, json, urllib.request
b64 = base64.b64encode(open("pic.png","rb").read()).decode()
body = json.dumps({"model":"qwen3-vl","messages":[{"role":"user","content":[
    {"type":"image_url","image_url":{"url":"data:image/png;base64,"+b64}},
    {"type":"text","text":"描述这张图"}]}],"max_tokens":1200}).encode()
req = urllib.request.Request("https://kilee.cn/api/vl/chat/completions", body,
    {"Authorization":"Bearer vl_xxx","Content-Type":"application/json"})
print(json.load(urllib.request.urlopen(req, timeout=300))["choices"][0]["message"]["content"])
PY
```

Python SDK：

```python
from openai import OpenAI
c = OpenAI(base_url="https://kilee.cn/api/vl", api_key="vl_xxx")
print(c.chat.completions.create(model="qwen3-vl",
      messages=[{"role":"user","content":"你好"}], max_tokens=1200).choices[0].message.content)
```

### 公开端点

| 请求 | 上游 |
|---|---|
| `POST /api/vl/chat/completions` | `/v1/chat/completions` |
| `GET  /api/vl/models` | `/v1/models` |

其余路径一律 `404`。已开 CORS（`Access-Control-Allow-Origin: *`），浏览器可直连。

流式请求会被**自动注入 `stream_options.include_usage = true`**（用于统计 token）；
客户端显式设了 `stream_options` 则尊重客户端。结果是流式响应末尾会多一个
`choices: []`、只带 `usage` 的 chunk —— 这是 OpenAI 的标准行为，主流 SDK 都能处理。

### 错误语义

| 状态 | `code` / `type` | 含义 |
|---|---|---|
| `401` | `invalid_api_key` | key 缺失/无效/已吊销/未生效 |
| `403` | `account_disabled` | key 所属账号被 owner 停用 |
| `429` | `rate_limit_error` | 超出该 key 的每日配额 |
| `503` | `upstream_unavailable` | 设备不存在、已下线、上游连接 error（**离线统一是 503**） |
| `404` | `invalid_request_error` | 路径不支持（注意 `/api/vl` 后不要再加 `/v1`，加了就 404） |

## 5. 接入自己的 DGX（一条命令）

1. 在控制台注册/登录 → 「我的设备」→「+ 添加设备」，拿到配对命令；
2. 在**自己的 DGX 上以 root 执行**：

   ```bash
   curl -fsSL https://kilee.cn/vl/agent.sh | sudo bash -s -- --pair <配对码> --upstream 8001
   ```

   agent 依次做四件事：

   - 本机生成 ed25519 私钥 `/etc/kilee-vl/agent_key`（`0600`，**只留本机，永不上传**）；
   - `POST /vl/api/devices/pair`，只上报**公钥**，换回隧道端口与服务器主机公钥指纹；
   - 把返回的 host key 指纹写进 `known_hosts` 并在之后强制 `StrictHostKeyChecking=yes`（首连即成信任锚，防中间人）；
   - 写 `/etc/kilee-vl/config` 与 `kilee-vl-tunnel.service`，`enable --now`，常驻 `ssh -R` 反向隧道。

   其它子命令：`sudo bash agent.sh status` / `sudo bash agent.sh uninstall`。

3. 配对是**异步**的：服务器侧的 provisioner 每 20s 把 `devices[]` 同步成 `authorized_keys`，
   `agent.sh` 会**最多等 90 秒**等授权（单元 `Restart=always` 也会持续重试）。
   如果配对失败，脚本会把服务器返回的原文透出，例如
   `配对失败（HTTP 409）：这台机器的公钥已经绑定到账号「xxx」…`。

4. 回到控制台，设备显示「在线」后创建 key（默认绑定该设备），即可用 §4 的 curl 调用。

## 6. 服务器侧安全模型

- 隧道账号 `kilee-tunnel`：系统账号、无 shell、无密码登录。
- `sshd_config` 末尾 `Match User kilee-tunnel`：
  `AllowTcpForwarding remote`（只许 `-R`，堵死 `-L`）、`PermitOpen none`、`GatewayPorts no`、
  `PermitTTY no`、`X11Forwarding no`、`AllowAgentForwarding no`、`ForceCommand /usr/sbin/nologin`。
- `authorized_keys` **逐密钥**锁定：

  ```
  restrict,port-forwarding,permitlisten="127.0.0.1:88XX",no-pty ssh-ed25519 AAAA... kilee-vl:<deviceId>
  ```

  `restrict` 关掉 PTY / agent / X11 / `~/.ssh/rc` 等一切，再只放开端口转发；
  `permitlisten` 把反向转发的目标**钉死在一个回环端口**上。

  > ⚠️ **不要写 `permitopen="none"`。** sshd 解析不了这个值会**整行静默拒绝**，
  > 表现为隧道永远 `Permission denied (publickey)`。禁止 `-L` 由 Match 块的 `PermitOpen none` 兜底即可。
  > （这一条是实测踩坑后写下的，见 §10。）

- `kilee-vl-provision.timer` 每 20s 幂等同步；吊销/删除设备即回收端口与公钥。
- 端口池 `8820-8899`；保留端口 `8801`、`8811` 不分配。
- 账号被停用 ⇒ 其所有 key 立即 `403`；删除账号连带删设备与 key。
- 控制台：登录失败 8 次 / 10 分钟 → 封 10 分钟；密码 scrypt（N=16384）；
  会话 cookie `HttpOnly; Secure; SameSite=Lax; Path=/vl`，HMAC-SHA256 签名，12h 过期；
  改密码轮换签名密钥 ⇒ 所有旧会话立即失效；
  状态变更接口要求 `X-Requested-With: vl` 头（配合 SameSite 防 CSRF）。
- 隐私：流量过境服务器（图片/对话会被中转）；不落盘媒体正文。

## 7. 环境变量

全部有默认值，只有需要时在 systemd 单元里显式设置（本仓库部署时的实际取值见括号）。

| 变量 | 默认 | 说明（线上取值） |
|---|---|---|
| `VL_HOST` | `127.0.0.1` | 网关监听地址 |
| `VL_PORT` | `8811` | 网关端口 |
| `VL_DATA_DIR` | `/var/lib/kilee-vl-admin` | `admin.json` / `keys.json` 所在目录 |
| `VL_SERVER_HOST` | `kilee.cn` | 返回给 agent 的跳板机域名 |
| `VL_SSH_PORT` | `22` | 跳板机 SSH 端口 |
| `VL_TUNNEL_USER` | `kilee-tunnel` | 隧道系统账号 |
| `VL_TUNNEL_PORT_MIN` / `MAX` | `8820` / `8899` | 端口池 |
| `VL_RESERVED_PORTS` | `8801,8811` | 不参与分配的端口 |
| `VL_DEFAULT_UPSTREAM_PORT` | `8001` | agent 默认转发的 DGX 本地端口 |
| `VL_MODEL` | `qwen3-vl` | 上游不可达时兜底展示的模型名 |
| `VL_OWNER_USER` | `owner` | 内置管理员账号名 |
| `VL_ALLOW_REGISTER` | `1` | 是否开放自助注册（线上：开放） |
| `VL_INVITE_CODE` | 空 | 设了即强制邀请制 |
| `VL_MAX_DEVICES_PER_ACCOUNT` | `5` | 每账号设备数上限（线上：`5`） |
| `VL_HOST_KEY_FP` | 自动读取 | 手动固定跳板机 host key 指纹 |

## 8. 部署落地路径

| 路径 | 说明 |
|---|---|
| `/opt/kilee-vl-admin/server.js` | 网关 + 控制台后端 |
| `/opt/kilee-vl-admin/init-admin.js` | 初始化脚本 |
| `/var/www/kilee.cn/vl/index.html` | 控制台前端（线上那份） |
| `/var/www/kilee.cn/vl/agent.sh` | 连接器脚本（公网可下载） |
| `/var/lib/kilee-vl-admin/admin.json` | 管理密码 hash + 会话签名密钥（`600`，内含 `accounts[]`） |
| `/var/lib/kilee-vl-admin/keys.json` | 账号 / 设备 / key / 配对码 / 用量（`600`） |
| `/usr/local/sbin/kilee-vl-provision.sh` | provisioner |
| `/etc/systemd/system/kilee-vl-admin.service` | 网关单元（user `kilee-vl-admin`） |
| `/etc/systemd/system/kilee-vl-provision.{service,timer}` | provisioner 单元 |
| `/etc/ssh/sshd_config` | 末尾含 `Match User kilee-tunnel` 段落 |
| `/var/lib/kilee-tunnel/.ssh/authorized_keys` | 由 provisioner **全量重写**，请勿手工编辑 |
| `/etc/nginx/sites-enabled/kilee.cn` | `location ^~ /api/vl/`、`/vl/`、`^~ /vl/api/` |

DGX 侧：

| 路径 | 说明 |
|---|---|
| `/etc/kilee-vl/agent_key` / `.pub` | 设备私钥 / 公钥（私钥 `0600`，只留本机） |
| `/etc/kilee-vl/known_hosts` | 跳板机 host key（配对时固定） |
| `/etc/kilee-vl/config` | 隧道参数 |
| `/etc/systemd/system/kilee-vl-tunnel.service` | 反向隧道单元（`enable`，开机自启） |

## 9. 运维

```bash
# ── 服务器：网关 ──
ssh root@<跳板机> 'systemctl status kilee-vl-admin; journalctl -u kilee-vl-admin -n 50 --no-pager'
ssh root@<跳板机> 'systemctl status kilee-vl-provision.timer'

# ── 服务器：核对隧道授权 ──
ssh root@<跳板机> 'cat /var/lib/kilee-tunnel/.ssh/authorized_keys'

# ── DGX：反向隧道 / vLLM ──
systemctl status kilee-vl-tunnel
/home/Developer/services/qwen3-vl/run.sh          # 重建并等待就绪（约 4 分钟）
/home/Developer/services/qwen3-vl/run.sh stop|logs
```

改任何配置文件前**先备份**；`sshd` / `nginx` 必须 `sshd -t` / `nginx -t` 通过后才 reload。

### 重置管理密码

服务器上备份后删掉 `admin.json`，重跑初始化会生成新密码（`keys.json` 保留）：

```bash
ssh root@<跳板机> '
  mv /var/lib/kilee-vl-admin/admin.json /root/admin.json.bak.$(date +%s)
  runuser -u kilee-vl-admin -- node /opt/kilee-vl-admin/init-admin.js /var/lib/kilee-vl-admin ""
  systemctl restart kilee-vl-admin'
```

### 回滚

1. 停控制台：`systemctl disable --now kilee-vl-admin kilee-vl-provision.timer`
2. 还原 nginx：`cp -a /root/nginx-kilee.cn.bak.<TS> /etc/nginx/sites-enabled/kilee.cn && nginx -t && systemctl reload nginx`
3. 还原 `sshd_config`：`cp -a /root/sshd_config.bak.<TS> /etc/ssh/sshd_config && sshd -t && systemctl reload sshd`
4. 还原数据：`/root/keys.json.bak.*` / `/root/keys.json.premigrate.*` / `/root/keys.json.pairbak.*`
5. DGX 侧：`systemctl disable --now kilee-vl-tunnel`

### 资源互斥（重要）

DGX Spark 上 Qwen3-VL（0.70 GPU util）与 qwen36-nvfp4（0.60）**不能同时跑**（超 128G 统一内存）：

```bash
docker stop vllm-qwen3vl && docker start vllm-qwen36   # 切回文本模型（监听 8000）
docker stop vllm-qwen36  && docker start vllm-qwen3vl  # 切回 VL（监听 8001）
```

因为互斥，`run.sh` **故意不加 `--restart`**，避免开机自动拉起和另一个模型撞车。
隧道固定转发到 `127.0.0.1:8001`；切到监听 8000 的模型后 `/api/vl/` 会 `503 upstream_unavailable`。

## 10. 踩坑与设计决策

### 10.1 同一把公钥只能属于一个账号（关键）

**现象**：新配对的设备一直显示离线，DGX 侧隧道反复 `activating (auto-restart)`，
退出码 255。

**根因**：同一台机器被配对到两个账号（owner 与某租户），`authorized_keys` 里出现
**两行完全相同的公钥**，只有 `permitlisten` 端口不同（8820 / 8821）。
sshd 对**同一把公钥只认第一条匹配行**，所以申请第二行才允许的那个端口时反向转发被拒，
`ssh -R` 立刻退出 → `Restart=always` 无限重启 → 控制台永远离线。
服务器日志里 publickey 认证每次都成功，只是会话建立后立刻被关闭，约 7 秒一轮 —— 极具迷惑性。

**修复（已落到代码，防止再次发生）**：

- `server.js`：配对时若该公钥已属于**别的账号**，直接返回
  `409 { code: "pubkey_in_use" }` 并写明原账号；同账号重复配对仍保持幂等；
- `deploy/kilee-vl-provision.sh`：写 `authorized_keys` 时**按公钥去重**（保留第一行，其余跳过并打日志）；
- `public/agent.sh`：把服务器返回的错误原文透出，不再只报笼统的失败。

### 10.2 防僵尸设备

同一台机器反复配对（重装系统、丢密钥）会在 `devices[]` 里堆出一批**永远离线**的记录，
白占隧道端口。对策：

- `VL_MAX_DEVICES_PER_ACCOUNT` 限制每账号设备数，超限返回 `409 { code: "device_limit" }`；
- `publicDeviceView` 计算 `stale`（**从未上线且创建 > 1h**，或**最后在线 > 24h**），
  `/vl/api/devices` 与 `/vl/api/overview` 都返回 `stale` 与 `maxDevicesPerAccount`；
- 控制台显示「`N / 上限 台设备，其中 M 台疑似僵尸`」，僵尸行加标注，
  并提供「清理 M 台疑似僵尸」按钮（列出 + 确认 + 批量删除）。

### 10.3 Thinking 模型的调用注意

- **模型名必须精确是 `qwen3-vl`**，否则 vLLM 返回
  `404 {"error":{"message":"The model \`xxx\` does not exist."}}`；
  客户端里预设的 `gpt-4o` / `claude-...` 之类都不行。
- **Thinking 模型先出 reasoning 再出 content**。`max_tokens` 给小（如 200）会被 reasoning 吃光，
  `content` 返回空 —— **建议 ≥ 800**。
- 流式下思考片段在 `delta.reasoning`，正文在 `delta.content`；非流式对应
  `message.reasoning` / `message.content`。
- `--max-num-seqs 1`：**单并发**，请求会排队。非流式调用请把客户端超时设到 **300s 以上**。
- 工具调用已开启（`--enable-auto-tool-choice --tool-call-parser hermes`）。
  选 `hermes` 是因为本模型 chat template 输出的是
  `<tool_call>{"name":…,"arguments":{…}}</tool_call>` 这种 JSON-in-XML；
  vLLM 另外的 `qwen3_xml` / `qwen3_coder` 是另一种 XML 风格，**不匹配本模型**。
- nginx 侧：`client_max_body_size 50m`、`proxy_read_timeout 300s`、
  限流 `vlapi`（60 r/m）/ `vladmin`（120 r/m）。

## 11. 数据模型（`keys.json`，version 2）

```jsonc
{
  "version": 2,
  "accounts": [ { "id", "username", "passwordHash", "role": "owner|tenant", "disabled" } ],
  "devices":  [ { "id", "accountId", "name", "tunnelPort", "upstreamPort",
                  "pubkey", "pubkeyFp", "models", "revokedAt", "lastSeenAt", "createdAt" } ],
  "keys":     [ { "id", "accountId", "deviceId", "label", "key",
                  "quota": { "maxRequestsPerDay": 0, "maxTokensPerDay": 0 },
                  "requests", "ok", "errors", "promptTokens", "completionTokens",
                  "dayStats": { "day", "requests", "tokens" } } ],
  "pairings": [ { "code", "accountId", "createdAt", "expiresAt", "usedAt" } ]
}
```

- `keys[].deviceId` 把 key 绑到设备 —— **key 决定路由**，所以对外 Base URL 天然统一。
- `quota` 中 `0` 表示不限。

### 管理接口一览（控制台前端用，均需会话 cookie）

| 方法与路径 | 说明 |
|---|---|
| `POST /vl/api/register` / `login` / `logout`，`GET /vl/api/session` | 账号（未鉴权；`session` 返回 `registerEnabled` / `inviteRequired`） |
| `GET /vl/api/overview` | 概览（设备、key 统计、用量汇总、`maxDevicesPerAccount`） |
| `GET /vl/api/devices` | 设备列表（含 `online` / `stale` / `models`） |
| `POST /vl/api/devices/pair/start` | 生成配对码，返回 `{ code, expiresAt, command, … }` |
| `POST /vl/api/devices/:id/revoke\|restore\|delete\|rename` | 设备操作 |
| `GET /vl/api/keys`、`POST /vl/api/keys` | key 列表 / 创建（`201` 返回明文 key，仅此一次） |
| `POST /vl/api/keys/:id/revoke\|restore\|rotate\|delete\|reset-stats\|quota` | key 操作（`rotate` 返回新明文 key） |
| `POST /vl/api/play` | 对话测试台（可带 `deviceId` 选设备，不消耗 key 额度） |
| `POST /vl/api/password` | 改密码 |
| `GET /vl/api/accounts`、`POST /vl/api/accounts/:id/disable\|enable\|delete` | owner 专属 |
| `POST /vl/api/devices/pair` | **未鉴权**，配对码即凭据，单次有效，TTL 10 分钟 |

状态变更类接口必须带 `X-Requested-With: vl` 头，否则 `400`。

## 12. 已验证

- 本地自测：主用例 **48/48** 全绿（401 / 200 / 429 / 503 / 409、CSRF 头、配对幂等、
  跨账号重复公钥、不同公钥共存）+ 加固专项 **10/10**（设备上限与 stale 判定）。
- 公网端到端：注册 → 登录 → 生成配对码 → 配对设备 → provisioner 下发受限公钥 →
  受限反向隧道上线 → 控制台显示在线 → 创建绑定设备的 key → 经该隧道真实推理 `200`。
- 负向：`-L` 本地转发不可达、拿不到 shell。
- `agent.sh` 的 root 安装路径已在本机实跑通（keygen → 配对 → 装 systemd 单元 →
  等待授权 → 隧道 active）。

---

_MIT License（跟随本仓库）。_
