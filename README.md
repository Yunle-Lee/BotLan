<img width="1024" height="306" alt="2bb536d100d8b81aa02d922d9aa517ba" src="https://github.com/user-attachments/assets/1112a865-20a4-4da7-a7b5-01fbaf2088f3" />

<img width="1163" height="920" alt="mockup" src="https://github.com/user-attachments/assets/4f65a0a4-7fc9-4690-9977-9b2ba4335d10" />

> Windows 桌面「胶囊 / 灵动岛」外壳，内嵌一个可管理本地文件的 Agent 聊天工作区。
> A Windows desktop "dynamic island" shell with an embedded, local-file-managing chat agent.

<p align="left">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%201809%2B%20x64-0078D6">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4">
  <img alt="Node.js" src="https://img.shields.io/badge/Node.js-22%2B-339933">
  <img alt="pnpm" src="https://img.shields.io/badge/pnpm-11.7-F69220">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

[下载安装包](https://github.com/Yunle-Lee/BotLan/releases/latest) · [English](#english)

---
## 官网 
https://kilee.cn/botlan/


## 简介

-在未来，每一个人都会有自己的DGX Spark类型的机器，本项目旨在创建一个Agent,让每一个DGX Spark节点成为一个Bot，让DGX节点集群成为一个农场。
-为用户提供了一个新颖独特的Agent交互和英伟达DGX Spark交互的面板与方式，旨在开启一种独特的交互与管理方式
-管理的不只是每一个DGX Spark节点，也是每一个Agent,把DGX Spark环境变得更个体，更智能，让Skills实现集市化
-IslandUI 把屏幕顶部的一小块区域变成一个可交互的「胶囊」：
-平时安静停靠，鼠标悬停展开，双击进入更大的 Long Island，再往下就是一个带 Bot 形象的 Chat 工作区。
-天界面不是外挂窗口，而是由本地 Node 后台驱动、通过 WebView2 嵌入的页面；后台负责会话、任务、工具调用与本地文件操作，模型请求由使用者自己配置。

界面语言为中英双语，代码与配置以本仓库为准。
# 说明书: 
## 项目说明

- 首先在你的DGX Spark上运行你的本地模型(当然你到时候也可以选择使用这个API key): 基座模型更新速度较快，每个人的需求也不一样，所以我们给用户宽广的选择
- 在主力电脑上下载本项目的桌面端面板
- https://kilee.cn/vl/ 访问官网的链接端，按照需求，注册登陆账号
- 登陆进去之后，点击[添加设备]，在DGX Spark上执行网站给你的对接指令
- 链接之后，即可使用DGX Spark本地跑的模型，支持TensorRT以及常见的vLLm,Sglang,llama.cpp
- 然后打开Botlan,双击可以切换到Long island,点击添加Bot,输入在[ https://kilee.cn/vl/]里面创建的API Key以及base url,即可使用DGX Spark本地运行的模型了
- 但是如果你没有本地模型的想法，你也可以直接在Bot创建的时候填入市面上主流的LLMs的API Key以及base url等诸多info
- 自定义Bot之后点击Bot pfp icon,进入Chat panel,即可对话，如果想要使用BotLan Agent处理这个DGX Spark这个本地的文件，需要到[代码工作区]链接你的DGX Spark
- 如果想要给Agent添加Skills,可以点击skills,直接一键部署skills给本地的Agent,提供了Nvidia的skills,以及awesome skills，如果你还有更多的需求，可以到我们官网的Skills广场上去逛逛
- 然后你就可以工作了，Botlan推荐把你每一个DGX Spark当作一个节点，每一个Bot就是一台DGX Spark
- 你可以非常方便地在右上角直接使用[鼠标滚轮去切换]，这个是我们开创的便捷方式，方便个人玩家或者是工作室管理DGX Spark农场
- 你还可以添加你的定时任务，填写健康信息，财务报告，让Agent融入到你的生活种

  <img width="1635" height="1198" alt="29fb3f1e0b69291c7c5b5ea8fab5f8a5" src="https://github.com/user-attachments/assets/bf17ac10-e6e3-4813-b9e8-1119a99611dd" />

  
## 使用事项
- 下载后是那个Long island状态，存在bot
- 双击后切换为small island状态
- 点击Bot先添加自己配置APi key 或者链接DGX Spark本地运行的模型
- 链接SSH的DGX Spark节点
  
## 注意事项
本产品以及作品目前用于且为其而生:第三届Nvidia DGX Spark Hackthon,迎合赛事需求，赛后开发者可以提供自己个性化的想法或者pr,随时欢迎

## 主要功能
**Skills 与英伟达DGX Spark **
- 源自Nvidia官方的Agent Skill的调制，一键部署，轻松上手
- Awesome Skills补齐剩下的Agent需求，让你的选择更加的全面

<img width="1503" height="833" alt="7eb31b850076f57ed44af01c4d06cf37" src="https://github.com/user-attachments/assets/681b3fe8-8161-4961-b212-78e6ad1fab33" />


**胶囊与岛**

- Small Island：停靠在桌面顶部，鼠标悬停展开；系统状态（电量、Wi-Fi）就地展示。
- Long Island：双击胶囊切换；滚轮在右上角滑轨选择 Bot。
- Chat 窗口：点击 × 时整窗向右对折收拢；胶囊离开悬停后在**当前位置**收形为 Long Island，不再跳回旧桌面坐标，拖动过胶囊后同样成立。

**Bot 动画**

- `BotAnimationEngine.Sample(t, state, options)` 纯函数式采样，按时间生成动画帧，不依赖 UI 时钟；`BotView` 只负责 WPF 绘制。
- 状态序列、形态、表情、视线拟合、皮肤与装饰可组合；关闭或隐藏时释放渲染回调。

**Chat 工作区**

- 会话与线程、产物（artifacts）、代码工作台、浏览器控制台、PDF 阅读、邮件工具卡片、日期时间编辑。
- 本地后台基于 Hono + PGlite（Postgres WASM），模型适配覆盖 OpenAI / Anthropic / Gemini 等兼容接口。
- 可选 Docker 计算容器（`Chat/apps/computer`）与浏览器容器（`Chat/apps/worker`），**不自动安装、不自动授权**，需自行准备。



## 下载与安装

前往 [Releases](https://github.com/Yunle-Lee/BotLan/releases/latest) 下载 `IslandUI-Setup.exe`。

- 支持 Windows 10 1809 及以上 x64，默认安装到当前用户目录，无需预装 .NET 或 Node.js。
- 系统缺少 WebView2 时，安装程序会引导安装微软官方组件，此时需要网络。
- 安装器会创建桌面与开始菜单快捷方式，卸载通过 Windows「已安装的应用」完成。
- 安装包**尚未使用发布者代码签名证书签名**，Windows 可能提示未知发布者；`docs/SHA256SUMS.txt` 中的 SHA-256 只能校验传输完整性，不等同于数字签名。

首次使用请从 Long Island 添加 Bot；API 地址、模型名与密钥由使用者自行填写，本仓库不含任何密钥。

## 从源码构建

**环境要求**：.NET 8 SDK、Node.js 22+（打包脚本按 24 验证）、pnpm 11.7。

```powershell
pnpm --dir Chat install --frozen-lockfile
pnpm --dir Chat run build:island
dotnet build IslandUI/IslandUI.csproj
& '.\IslandUI\bin\Debug\net8.0-windows\IslandUI.exe'
```

调试版可加 `--chat` 直接打开已配置 Bot 的 Chat。只想改前端时，可以用 `pnpm --dir Chat run dev:mobile` 在浏览器里迭代页面。

## 打包安装器

需要 Inno Setup 6.7+ 编译器，以及从微软官方下载的 WebView2 引导程序（脚本会校验其 Authenticode 签名必须来自 Microsoft）。

```powershell
.\Packaging\Build.ps1 -InnoCompiler 'C:\Tools\InnoSetup\ISCC.exe' `
                      -WebViewBootstrapper 'C:\Tools\MicrosoftEdgeWebview2Setup.exe'
```

用于隔离部署测试的参数：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PORTABLE=1 /DIR="测试目录"`。便携模式不创建快捷方式、不写卸载注册项、不自动启动。详见 [`Packaging/README.md`](Packaging/README.md)。

## 目录结构

```
IslandUI/          C#/WPF 桌面宿主：胶囊、Long Island、Chat 窗口、Bot 绘制与系统状态
  Bots/            Bot 动画引擎（采样、状态、形态、表情、皮肤）
  Controls/        自定义控件（书页折叠、弧形标签、弹性动画、像素加载等）
  Services/        系统状态、Bot 配置存储、Chat 运行时
  Resources/       Maple Mono 字体
Chat/              pnpm 工作区：嵌入页面 + 本地后台
  apps/mobile/     Expo / React Native Web 界面（同时用于 WebView 嵌入）
  apps/server/     Hono 本地服务：会话、任务、工具、数据库、模型适配
  apps/worker/     可选的浏览器容器
  apps/computer/   可选的 Docker 计算容器
  packages/        domain / backends / integrations 共享包
  scripts/         构建辅助脚本
  tests/           集成测试
Packaging/         发布构建与 Inno Setup 安装器脚本
.verify-bots/      Bot 动画引擎校验程序
.verify-chat/      折叠、启动与 Studio 冒烟校验程序
licenses/          第三方许可全文（npm 依赖、字体、WebView2 等）
docs/              交付说明、审查记录与源码校验清单
```

## 数据与隐私

| 场景 | 位置 |
| --- | --- |
| 安装版用户数据 | `%LOCALAPPDATA%\IslandUI` |
| 源码版 Chat 数据 | `Chat/.island-data` |

这两处都不应提交或分发，已在 `.gitignore` 中排除。卸载程序默认保留用户数据。

本仓库不包含 `.env`、真实 API 密钥、聊天数据库、WebView 用户目录、`node_modules` 或编译产物。历史上使用过的密钥建议在服务商后台撤销或轮换。

## 测试

```powershell
# 前端单元测试 + 集成测试
pnpm --dir Chat test
pnpm --dir Chat run test:island

# Bot 动画引擎校验
dotnet run --project .verify-bots/BotsVerify.csproj

# 折叠定位、启动与 Studio 冒烟校验
dotnet run --project .verify-chat/ChatPreview.csproj
```

## 第三方与许可

本项目以 MIT 许可发布，见 [LICENSE](LICENSE)。

- `IslandUI/Resources/MapleMono-CN-*.ttf` 为 **Maple Mono** 字体，使用 SIL Open Font License 1.1。
- .NET、WebView2、Node.js 及 JavaScript 依赖遵循各自许可。

完整声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 [`licenses/`](licenses/)。

## 已知限制

- 安装器未做发布者数字签名；缺少 WebView2 的电脑首次补装需要网络。
- 未在缺少 WebView2 的全新离线电脑、多显示器混合 DPI 环境下验证。
- 可选 Docker 环境与第三方账号连接未做外部集成测试。
- 编译仍有原有 `SmallIslandWindow.Icon` 隐藏基类属性的警告；依赖锁文件保留既有版本，未为消除弃用警告而批量升级。

---

## English

**IslandUI** turns a small strip at the top of your Windows desktop into an interactive island: it rests quietly, expands on hover, opens a larger *Long Island* on double-click, and hosts a chat workspace with an animated bot avatar. The chat UI is not a separate window — it is a page embedded through WebView2 and served by a local Node backend that owns conversations, tasks, tool calls and local file operations. Model endpoints and keys are configured by the user; none ship with this repository.

**Highlights**

- **Island shell** — hover-to-expand capsule with battery/Wi-Fi readout; double-click to switch to Long Island; mouse wheel picks a bot on the top-right rail.
- **Fold & restore** — clicking × folds the chat window to the right; when the capsule leaves hover it collapses into a Long Island *at its current position*, including after being dragged.
- **Bot animation** — `BotAnimationEngine.Sample(t, state, options)` samples frames from time as a pure function; `BotView` only draws. Shapes, expressions, gaze fitting, skins and decorations compose freely.
- **Chat workspace** — threads, artifacts, code workbench, browser console, PDF reader, mail tool cards; local backend on Hono + PGlite with OpenAI / Anthropic / Gemini adapters.
- **Optional containers** — Docker compute and browser workers under `Chat/apps/`; nothing is installed or authorized automatically.

**Getting started**

Download `IslandUI-Setup.exe` from [Releases](https://github.com/Yunle-Lee/BotLan/releases/latest) (Windows 10 1809+ x64, no .NET or Node.js required). The installer is **not code-signed**, so Windows may warn about an unknown publisher.

To build from source you need the .NET 8 SDK plus Node.js 22+ and pnpm 11.7:

```powershell
pnpm --dir Chat install --frozen-lockfile
pnpm --dir Chat run build:island
dotnet build IslandUI/IslandUI.csproj
& '.\IslandUI\bin\Debug\net8.0-windows\IslandUI.exe'
```

Installed builds keep user data in `%LOCALAPPDATA%\IslandUI`; source builds use `Chat/.island-data`. Neither belongs in version control. This repository ships no `.env` files, API keys, chat databases, WebView profiles, `node_modules` or build output.

See [LICENSE](LICENSE), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [`licenses/`](licenses/) for licensing. The bundled Maple Mono fonts are under SIL OFL 1.1; .NET, WebView2, Node.js and the JavaScript dependencies keep their respective licenses.

---

## 附加模块：DGX VL API（把本地 DGX Spark 模型变成公网 OpenAI 兼容 API）

> 目录：[`dgx-vl-api/`](dgx-vl-api/) · 详细文档：[`dgx-vl-api/README.md`](dgx-vl-api/README.md)

把 DGX Spark 上本地运行的 vLLM 多模态模型（`Qwen3-VL-30B-A3B-Thinking`，BF16）经跳板机
暴露为**公网 OpenAI 兼容 API**，并配一个带 API key 管理的网页控制台。
支持**多台 DGX / 多个账号**，对外共用同一个 Base URL，由 API key 决定路由到哪台设备。

| | |
| --- | --- |
| 控制台 | <https://kilee.cn/vl/> |
| Base URL | `https://kilee.cn/api/vl` |
| 模型名 | `qwen3-vl`（必须精确是这个值） |

**核心设计：永远 outbound。** DGX 主动向跳板机建 SSH 反向隧道，服务器**从不主动连 DGX**，
只保存公钥、**私钥永不离开对方机器**；隧道账号被 `authorized_keys` 的 `permitlisten`
钉死在单个回环端口，`sshd` 的 `Match` 块堵死本地转发与 shell。

**新增能力**

- **多租户**：自助注册/登录 → 「添加设备」拿到一条命令 → 在自己的 DGX 上以 root 执行即完成配对；
  对外 Base URL 不变，`keys[].deviceId` 决定路由。
- **控制台**：概览、设备管理（在线探测、上线轮询、僵尸设备识别与一键清理）、
  API key 全生命周期（创建 / 轮换 / 吊销 / 恢复 / 删除 / 清零统计）、
  每 key 每日配额、在线对话测试台（支持看图与流式）、owner 账号管理。
- **网关**：`POST /api/vl/chat/completions`、`GET /api/vl/models`，负责 key 校验、用量统计与按 key 路由；
  统一错误语义（`401 invalid_api_key` / `403 account_disabled` / `429 rate_limit_error` /
  `503 upstream_unavailable`）。
- **防重复配对与防僵尸设备**：一把公钥只能属于一个账号，跨账号重复配对返回 `409 pubkey_in_use`；
  每账号设备数上限返回 `409 device_limit`；长期离线的设备会在控制台被标记并可一键清理。

**文件**（全部在新目录 `dgx-vl-api/`，与主项目的 `IslandUI` / `Chat` 互不影响）

```
dgx-vl-api/server.js                 网关 + 控制台后端（纯 Node 内置模块，零依赖）
dgx-vl-api/init-admin.js             一次性初始化：生成管理密码、迁移数据
dgx-vl-api/public/index.html         控制台前端（单文件 SPA）
dgx-vl-api/public/agent.sh           DGX 侧连接器：生成密钥 → 配对 → 装反向隧道
dgx-vl-api/deploy/                   provisioner 与 sshd Match 片段
dgx-vl-api/kilee-vl-admin.service    网关的 systemd 单元
dgx-vl-api/patch-nginx.py            给 nginx 站点打补丁
dgx-vl-api/DESIGN-multitenant.md     多租户设计草案
dgx-vl-api/README.md                 完整文档（架构 / 接口 / 安全 / 运维 / 踩坑）
```

**调用示例**

```bash
curl https://kilee.cn/api/vl/chat/completions \
  -H "Authorization: Bearer vl_xxx" -H 'Content-Type: application/json' \
  -d '{"model":"qwen3-vl","messages":[{"role":"user","content":"你好"}],"max_tokens":1200}'
```

```python
from openai import OpenAI
c = OpenAI(base_url="https://kilee.cn/api/vl", api_key="vl_xxx")
print(c.chat.completions.create(model="qwen3-vl",
      messages=[{"role":"user","content":"你好"}], max_tokens=1200).choices[0].message.content)
```

> 本仓库不含任何真实密钥。Thinking 模型注意：`max_tokens` 建议 ≥ 800（否则会被 reasoning 吃光），
> `--max-num-seqs 1` 为单并发，非流式调用请把客户端超时设到 300s 以上。

**Addition (English) — DGX VL API.** See [`dgx-vl-api/`](dgx-vl-api/). It exposes a locally hosted
vLLM multimodal model on a DGX Spark as a public OpenAI-compatible API
(`https://kilee.cn/api/vl`, model `qwen3-vl`), together with a web console for API-key
management, per-key daily quotas, device pairing and usage stats. It is multi-tenant: many
devices and accounts share one base URL, and the API key selects the route. The design is
outbound-only — the DGX dials out to the jump host over a restricted SSH reverse tunnel, so the
server never connects back and never holds a private key.
