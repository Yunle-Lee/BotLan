<img width="1024" height="306" alt="BotLan banner" src="https://github.com/user-attachments/assets/1112a865-20a4-4da7-a7b5-01fbaf2088f3" />

<img width="1163" height="920" alt="BotLan mockup" src="https://github.com/user-attachments/assets/4f65a0a4-7fc9-4690-9977-9b2ba4335d10" />

# BotLan：以 DGX Spark 为节点的桌面 Agent 农场

> 一台 DGX Spark 就是一个 Bot，一组 DGX Spark 就是一座农场。
> BotLan 是一个 Windows 桌面「灵动岛」形态的 Agent 客户端：它把每台 DGX Spark 上本地运行的模型接入同一个桌面入口，并通过可按 Bot 独立安装的 **Agent Skills**，让每个 Bot 掌握部署、运维与使用 NVIDIA 技术栈和 StepFun（阶跃星辰）开源模型的专业能力。

<p align="left">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%201809%2B%20x64-0078D6">
  <img alt="DGX Spark" src="https://img.shields.io/badge/NVIDIA-DGX%20Spark%20GB10-76B900">
  <img alt="StepFun" src="https://img.shields.io/badge/StepFun-GELab--Zero--4B%20%7C%20Step--3.7--Flash-5B5BD6">
  <img alt="Agent Skills" src="https://img.shields.io/badge/Agent%20Skills-NVIDIA%2Fskills-76B900">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4">
  <img alt="Node.js" src="https://img.shields.io/badge/Node.js-22%2B-339933">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

[下载安装包](https://github.com/Yunle-Lee/BotLan/releases/latest) · [官网](https://kilee.cn/botlan/) · [DGX 网关控制台](https://kilee.cn/vl/) · [English](#english)

本项目为 **第三届 NVIDIA DGX Spark Hackathon** 参赛作品（已进入决赛）。

---

## 目录

1. [项目定位](#1-项目定位)
2. [为什么是 DGX Spark](#2-为什么是-dgx-spark)
3. [技术栈总览](#3-技术栈总览)
4. [Agent Skills 体系](#4-agent-skills-体系)
5. [StepFun 阶跃星辰模型集成](#5-stepfun-阶跃星辰模型集成)
6. [系统架构](#6-系统架构)
7. [快速开始](#7-快速开始)
8. [功能详解](#8-功能详解)
9. [安全设计](#9-安全设计)
10. [从源码构建与测试](#10-从源码构建与测试)
11. [目录结构](#11-目录结构)
12. [数据与隐私](#12-数据与隐私)
13. [已知限制](#13-已知限制)
14. [第三方与许可](#14-第三方与许可)

---

## 1. 项目定位

个人与小型工作室正在拥有越来越多的本地 AI 算力。DGX Spark 把一台桌面级设备的可用内存推到 128 GB，使“在自己桌上跑百亿乃至近两千亿参数模型”成为现实。随之而来的问题是：**多台 Spark、多个模型、多套推理引擎，如何被一个人高效地管理和使用？**

BotLan 的回答由三层组成：

| 层 | 作用 | 对应实现 |
| --- | --- | --- |
| **节点层** | 每台 DGX Spark 在本地运行推理服务（TensorRT-LLM / NIM / vLLM / SGLang / llama.cpp），对外暴露 OpenAI 兼容接口 | 用户自选引擎；BotLan 技能提供标准化部署流程 |
| **连接层** | 把 Spark 上的本地接口安全地送到用户的主力电脑，无需公网 IP、无需开放入站端口 | `dgx-vl-api/`：受限 SSH 反向隧道 + 多租户 API Key 网关 |
| **交互层** | 桌面灵动岛 + Chat 工作区；一个 Bot 对应一台 Spark，滚轮即可切换；每个 Bot 拥有独立的会话、记忆、任务与技能 | `IslandUI/`（WPF）+ `Chat/`（WebView2 内嵌页面与本地 Node 后台） |

## 2. 为什么是 DGX Spark

BotLan 的设计直接建立在 DGX Spark 的硬件特性之上，而不是把它当作一台普通的远程 GPU 服务器：

| DGX Spark 特性 | BotLan 如何利用 |
| --- | --- |
| **GB10 Grace Blackwell 超级芯片，128 GB CPU–GPU 统一内存** | 单机即可承载 StepFun Step-3.7-Flash（198B MoE）的 4-bit GGUF 量化版本，或同时驻留多模态小模型与大语言模型。`spark-model-planner` 技能按统一内存给出逐项内存账本，而不是沿用“显存/内存分离”的旧估算方式。 |
| **Blackwell 原生 FP4（NVFP4）** | `spark-trtllm-serve` 技能优先选择 NVIDIA 发布的 FP8 / NVFP4 检查点（如 `nvidia/Qwen3-30B-A3B-FP4`、`nvidia/NVIDIA-Nemotron-3-Super-120B-A12B-NVFP4`），以 TensorRT-LLM 获得更高吞吐。 |
| **DGX OS + Docker + NVIDIA Container Toolkit 预装** | 所有部署技能均以官方容器（`nvcr.io/nvidia/tensorrt-llm/release`、DGX Spark 专用 NIM、vLLM 容器）为基础，命令可复现、可回滚。 |
| **桌面形态、本地数据不出机** | 推理在用户自己的 Spark 上完成；网关只做加密转发与鉴权，不保存对话内容。 |
| **统一内存里多个模型同时常驻** | Jev-Style-0.8B（0.53 GB）和 GELab-Zero-4B（约 2.5 GB）同时常驻，互不挤占，还给用户自己的大模型留下空间；BotLan 网关先用 Jev 选技能（约 28 ms），再交给 4B 执行（实测约 76 tok/s）。 |
| **一台 Spark 分给多个 Bot** | 每个分区是一个 systemd 用户 slice（MemoryMax / CPUQuota / TasksMax）加 Landlock 文件范围，在滑轨上对应一个 Bot。 |
| **多台 Spark 可通过 QSFP 互联** | BotLan 的“农场”模型把每台 Spark 映射为一个 Bot，灵动岛右上角滚轮切换；模型选型技能会在需要时指向 NVIDIA 的双机互联 Playbook。 |

## 3. 技术栈总览

### NVIDIA

| 组件 | 在 BotLan 中的角色 |
| --- | --- |
| **DGX Spark / GB10** | 推理节点硬件；BotLan 的“一台 Spark = 一个 Bot”抽象的物理基础 |
| **TensorRT-LLM** | 高吞吐推理路径（`trtllm-serve`，Playbook 端口 8355/8356），由 `spark-trtllm-serve` 技能引导部署 |
| **NVIDIA NIM** | 开箱即用的推理微服务（`nvcr.io/nim/...-dgx-spark`，端口 8000），由 `spark-nim-deploy` 技能引导部署 |
| **NVFP4 / ModelOpt 量化** | 模型选型与内存估算的核心依据 |
| **NVIDIA Agent Skills（[github.com/NVIDIA/skills](https://github.com/NVIDIA/skills)）** | 官方技能目录。BotLan 在安装时**实时拉取上游 `SKILL.md`**，覆盖 RAG Blueprint、NeMo Retriever、Nemotron 语音、Dynamo、cuOpt、cuDF、NemoClaw 等 |
| **DGX Spark Playbooks（[build.nvidia.com/spark](https://build.nvidia.com/spark)）** | 内置部署技能的事实来源（镜像版本、端口、验证步骤） |
| **CUDA / llama.cpp CUDA 后端** | StepFun GGUF 模型在 Spark 上的运行路径 |

### StepFun 阶跃星辰

| 模型 | 规模 | 在 BotLan 中的角色 |
| --- | --- | --- |
| **Step-3.7-Flash** | 约 198B 总参数稀疏 MoE，每 token 激活约 11B，256K 上下文，支持图像输入，Apache-2.0 | 单台 Spark 上的旗舰“大脑”Bot（GGUF IQ4_XS ≈ 105 GB） |
| **Step3-VL-10B** | 10B 视觉语言模型（BF16 ≈ 20 GB），Apache-2.0 | 看图 / OCR / 界面理解 Bot，可与大模型同机共存 |

详见[第 5 节](#5-stepfun-阶跃星辰模型集成)。

### 应用层

| 组件 | 说明 |
| --- | --- |
| C# / WPF（.NET 8） | 灵动岛宿主、Bot 动画引擎、系统状态、Chat 窗口折叠 |
| WebView2 | 内嵌 Chat 页面 |
| Expo / React Native Web | Chat 界面（桌面嵌入与浏览器调试共用一套代码） |
| Hono + PGlite（Postgres WASM） | 本地后台：会话、任务、记忆、技能、工具调用 |
| CopilotKit Runtime + AG-UI + TanStack AI | Agent 运行时与流式事件协议；模型适配 OpenAI 兼容 / Anthropic / Gemini |
| Node.js 内置模块（零依赖） | `dgx-vl-api` 网关与控制台后端 |

## 4. Agent Skills 体系

技能（Skills）是 BotLan 本次决赛版本的核心升级。

### 4.1 设计原则

1. **遵循 Agent Skills 规范。** 每个技能是一份带 `name` / `description` 元数据的 Markdown 说明，与 NVIDIA 官方 `NVIDIA/skills` 目录及 Claude Code、Codex 等主流 Agent 使用的格式一致，可以互相迁移。
2. **按 Bot 独立安装。** 技能保存在本地后台数据库中，以 Bot（即 Spark 节点）为单位隔离。给“推理节点 A”装上 TensorRT-LLM 技能，不会影响“视觉节点 B”。
3. **渐进式加载（progressive disclosure）。** 对话时，系统提示词只包含已启用技能的**目录**（id、名称、一句话摘要）。当用户请求命中某个技能时，Agent 调用 `load_skill` 工具读取完整说明再执行。这样即使安装了很多技能，也不会撑爆上下文，对本地部署的模型尤其重要。
4. **技能是指导，不是授权。** 技能文本无法覆盖系统安全规则：外部写操作仍需原生审批；工具结果和文档仍被视为不可信数据；Agent 仍不得声称执行了它没有执行的命令。
5. **可审计。** 每个已安装技能记录来源 URL、版本和所注入说明的 SHA-256 摘要，在技能页可见。

### 4.2 技能来源

| 来源 | 说明 | 安装行为 |
| --- | --- | --- |
| **BotLan 内置** | 面向 DGX Spark 与 StepFun 模型、由本项目编写的技能 | 随应用分发，离线可用 |
| **NVIDIA 官方** | 精选自 `github.com/NVIDIA/skills` | 安装时从 `raw.githubusercontent.com/NVIDIA/skills` 实时拉取 `SKILL.md`，解析 frontmatter 后保存 |
| **技能广场** | `https://kilee.cn/api/skills` 社区目录 | 逐条做结构校验，不合规的条目被丢弃；广场不可达时其余来源仍可用 |
| **URL 导入** | 任意公开 HTTPS 的 `SKILL.md`；GitHub 页面链接自动转换为原始文件 | 必须包含 `name` 与 `description`；拒绝 http、内网地址、带凭据的 URL；单文件上限 256 KB |

### 4.3 内置技能

| ID | 名称 | 用途 |
| --- | --- | --- |
| `spark-model-planner` | DGX Spark 模型选型规划 | 按 128 GB 统一内存给出权重 + KV Cache 账本，推荐模型、精度与引擎，并指向下一步该装的技能 |
| `stepfun-step37-flash-spark` | StepFun Step-3.7-Flash 单机部署 | StepFun llama.cpp 分支编译、IQ4_XS 量化下载、仅本机监听并带 API Key 的服务启动、接入 Bot |
| `stepfun-step3-vl-spark` | StepFun Step3-VL-10B 视觉推理 | vLLM 部署视觉模型，控制 `--gpu-memory-utilization` 以便与大模型共存 |
| `spark-trtllm-serve` | TensorRT-LLM NVFP4 推理服务 | 按 NVIDIA Playbook 拉取镜像、校验 GPU、`trtllm-serve` 发布 OpenAI 接口 |
| `spark-nim-deploy` | NVIDIA NIM 一键推理微服务 | NGC 登录、DGX Spark 专用 NIM 容器运行与验证 |
| `spark-node-health` | DGX Spark 节点体检 | 标准化巡检清单（驱动、容器 GPU 通路、统一内存、磁盘、端口暴露），可设为定时任务 |
| `botlan-spark-gateway` | BotLan Spark 网关配对 | 通过 kilee.cn/vl 完成设备配对、API Key 管理与 Bot 接入 |

### 4.4 NVIDIA 官方技能（精选）

`rag-blueprint`、`nemo-retriever`、`nemotron-voice-agent-builder`、`dynamo-troubleshoot`、`cuopt-install`、`accelerated-computing-cudf`、`portfolio-optimization`、`nemoclaw-user-guide`。其他官方技能可通过“从 URL 导入”直接安装，例如：

```
https://github.com/NVIDIA/skills/blob/main/skills/<skill-name>/SKILL.md
```

### 4.5 执行边界：Skill → Agent → 审批 → Spark

Bot 接入 **BotLan 网关**（[BotLan-CLI](https://github.com/Yunle-Lee/BotLan-CLI) 的 `botlan_gateway.py`，Spark 上 `127.0.0.1:8091`）后，技能里的命令由 Agent 直接在 Spark 上执行：

1. **Jev-0.8B 选技能**：网关把用户请求和该 Bot 已装技能交给 Jev 决策模型（约 30 ms，不生成），把命中的技能作为提示交给 4B。Chat 顶部显示 `Jev-0.8B → spark-node-health · 0.95 · 28ms`。
2. **StepFun GELab-Zero-4B 照技能执行**：先 `load_skill`，再按技能步骤调用 `run_spark_command`，一次一条命令并附理由。
3. **人工审批**：每条命令都在 BotLan 里弹出“在 DGX Spark 上执行？”卡片，原样显示命令；只有点“批准执行”才会运行。拒绝、5 分钟无人处理或对话中断都视为拒绝。模型无法自己批准。
4. **回执**：退出码与输出（截断、超时如实标注）回到对话，Agent 根据真实输出决定下一步。

两个模型常驻同一块 128 GB 统一内存（约 0.5 GB + 2.5 GB 权重），不挤占用户自己跑的大模型。未接入网关的 Bot 保持原行为：私有计算容器**断网、无 GPU**，Agent 给出命令、用户在 Spark 终端执行后回贴输出。无论哪种方式，技能都要求 Agent 不得在没有输出证据时声称“已部署成功”。

### 4.5.1 分区：一台 Spark，多个 Bot

在 Spark Bot 的状态条里点“分区管理”，填写名称、内存 GB、CPU%，即可创建一个分区并自动在灵动岛滑轨里多出一个 Bot。每个分区是 Spark 上的一个 systemd 用户 slice：

| 项 | 是否硬限制 | 说明 |
| --- | --- | --- |
| 内存（CPU 侧） | 是（`MemoryMax`） | 实测 1 GB 分区里申请 3 GB 被 OOM 终止，回执标注“触及分区内存上限” |
| CPU | 是（`CPUQuota`） | 100% = 1 核 |
| 进程数 | 是（`TasksMax`） | 默认 512 |
| 工作目录 | 是 | 命令只能在 `~/botlan-zones/<id>` 内执行 |
| GPU 显存 | **否** | CUDA 分配不计入 cgroup（实测 1 GB 上限下照样加载 2.5 GB 模型）；分区 Bot 的提示词会告知预算，由它在启动引擎时设置 `--gpu-memory-utilization` / `-c` |

分区 Key 只交给桌面宿主保存（网页拿到的是一次性领取码），网关只存 SHA-256；分区 Bot 不能创建或删除分区。所有分区共用一个 Unix 用户，所以分区是**资源划分，不是 Bot 之间的安全隔离**。删除分区会结束它启动的全部进程，工作目录保留。

### 4.6 接口

技能由本地后台管理，所有接口都需要当前 Bot 的会话令牌：

| 方法 | 路径 | 作用 |
| --- | --- | --- |
| GET | `/api/agent/skills` | 列出目录与当前 Bot 的安装状态（不返回完整说明） |
| GET | `/api/agent/skills/:id` | 读取已安装技能的完整说明与摘要 |
| POST | `/api/agent/skills/:id/install` | 安装（NVIDIA 技能此时拉取上游） |
| POST | `/api/agent/skills/:id/enabled` | `{ "enabled": boolean }` 启用/停用 |
| POST | `/api/agent/skills/:id/uninstall` | 卸载 |
| POST | `/api/agent/skills/import` | `{ "url": "https://..." }` 从 URL 导入 |

环境变量 `SKILLS_MARKET_URL` 可替换技能广场地址，设为 `off` 可完全关闭广场请求（离线演示或内网部署）。

## 5. StepFun 阶跃星辰模型集成

以下参数均来自 StepFun 在 Hugging Face 上发布的模型卡；速度数据为厂商在 DGX Spark 上的实测，BotLan 未独立复测。

### 5.1 Step-3.7-Flash：单台 Spark 上的旗舰模型

- 稀疏 MoE 视觉语言模型，约 198B 总参数（196B 语言主干 + 1.8B 视觉编码器），每 token 激活约 11B，原生 256K 上下文，三档推理强度，Apache-2.0。
- BF16 / FP8 权重约 200 GB 以上，超出单台 Spark；NVFP4 版本的官方示例使用 4 卡张量并行。
- **单机路径为 GGUF + StepFun 的 llama.cpp 分支（`step3.7`）**：

| 量化 | 文件大小 | DGX Spark 上下文（厂商实测） |
| --- | --- | --- |
| IQ4_XS | ≈ 105 GB（+ mmproj ≈ 4 GB） | 262K，空上下文生成约 24 tok/s |
| Q4_K_S | ≈ 112 GB（+ mmproj ≈ 4 GB） | 约 131K |
| Q3_K_M / IQ3_XXS | ≈ 94 GB / 76 GB | 更多余量，精度有所下降 |

```bash
git clone https://github.com/stepfun-ai/llama.cpp.git && cd llama.cpp
git checkout -b step3.7 origin/step3.7
cmake -B build -DGGML_CUDA=ON -DLLAMA_BUILD_SERVER=ON
cmake --build build --config Release -j$(nproc)

export BOTLAN_LOCAL_KEY=$(openssl rand -hex 24)
./build/bin/llama-server -m ~/models/step37/<IQ4_XS>.gguf \
  --mmproj ~/models/step37/<mmproj-f16>.gguf --alias step-3.7-flash \
  -c 131072 -ngl 99 -fa on --host 127.0.0.1 --port 8080 --api-key "$BOTLAN_LOCAL_KEY"
```

### 5.2 Step3-VL-10B：视觉 Bot

- 10B 参数（1.8B PE-lang 视觉编码器 + Qwen3-8B 解码器），BF16 约 20 GB，运行时约 24 GB；模型卡要求 `--trust-remote-code` 与较新的 vLLM。
- 模型卡报告 OCRBench 86.75、MMMU 78.11、ScreenSpot-V2 92.61，适合截图理解、文档 OCR 与 GUI 定位。

```bash
vllm serve stepfun-ai/Step3-VL-10B --served-model-name step3-vl --trust-remote-code \
  --reasoning-parser deepseek_r1 --enable-auto-tool-choice --tool-call-parser hermes \
  --gpu-memory-utilization 0.3 --host 127.0.0.1 --port 8000 --api-key "$BOTLAN_LOCAL_KEY"
```

### 5.3 推荐的单机组合

一台 Spark 同时运行 Step3-VL-10B（约 24 GB）与一个 30B 级 NVFP4 文本模型，或者独占运行 Step-3.7-Flash IQ4_XS。前者适合“看图 + 对话”的双 Bot，后者适合长上下文推理。具体数字请让 `spark-model-planner` 按实际上下文长度计算。

## 6. 系统架构

```
┌──────────────────────── Windows 笔记本 ────────────────────────┐
│ IslandUI (WPF .NET 8)                                            │
│   小岛 / Long Island / Chat 窗口（右上角弧形滑轨切换 Bot）        │
│   「连接 DGX Spark」：SSH.NET 免密登录 + 应用自己维护的隧道       │
│        │ WebView2                                                │
│        ▼                                                         │
│ Chat 页面 (Expo / React Native Web)                               │
│   工作流时间线 · 审批卡片 · 附件 · 速度/上下文统计                 │
│        │ Bearer 会话令牌                                          │
│        ▼                                                         │
│ 本地 Node 后台 (Hono + PGlite，只监听 127.0.0.1)                   │
│   会话 / 记忆 / 任务 / 技能 · Agent 运行时 (CopilotKit + TanStack AI)│
└───────────────┬─────────────────────────────────────────────────┘
                │ SSH 本地端口转发 → 127.0.0.1:8091（OpenAI 兼容）
                ▼
┌──────────────────────── DGX Spark (GB10) ───────────────────────┐
│ BotLan 网关 :8091  —— Jev 选技能 · 审批后执行 · 遥测 · 分区        │
│   ├─► orchestrator :8090  Jev-Style-0.8B（判别，不生成，约 28 ms） │
│   └─► llama-server :8080  StepFun GELab-Zero-4B（视觉语言，约 76 tok/s）│
│ 分区 = systemd slice（内存/CPU/进程数）+ Landlock（文件范围）      │
└──────────────────────────────────────────────────────────────────┘
```

Spark 端的代码、安装方式和全部实测数据在 [BotLan-CLI](https://github.com/Yunle-Lee/BotLan-CLI)。

一次对话的执行流程：

1. 用户在 Bot 的 Chat 里说「检查一下 Spark 的健康状况」。
2. 网关把请求和这个 Bot 已安装的技能交给 Jev，Jev 选出 `spark-node-health`（约 28 ms，置信度 0.95），作为提示交给 4B。
3. 4B 调用 `load_skill` 读取技能全文，再按步骤调用 `run_spark_command`，每次一条命令并写明理由。
4. BotLan 弹出「在 DGX Spark 上执行？」卡片，原样显示命令；用户点「批准执行」后才会运行。拒绝、5 分钟无人处理、对话中断都算拒绝。
5. 命令在这个 Bot 的分区里执行，退出码和输出回到对话，4B 根据真实输出决定下一步。工作流时间线显示每一步和耗时。

## 7. 快速开始

1. **在 DGX Spark 上安装（约 10 分钟）**：

   ```sh
   curl -fsSL https://raw.githubusercontent.com/Yunle-Lee/BotLan-CLI/main/install.sh | sh
   ```

   它会检查机器、从 ModelScope 下载编译好的运行程序和模型（不用编译）、启动网关，然后打开配置向导：给第一个 Bot 起名、在方块图上拉框选活动范围、选模型后端、分配内存和 CPU。详见 [BotLan-CLI](https://github.com/Yunle-Lee/BotLan-CLI)。没有网络时可以插 BotLan U 盘离线安装。
2. **在 Windows 上安装 BotLan**：从 [Releases](https://github.com/Yunle-Lee/BotLan/releases/latest) 下载安装包。
3. **连接**：托盘菜单「连接 DGX Spark…」（Long Island 没有 Bot 时胶囊里也有这个入口）→ 填 `用户名@Spark地址` 和端口 → 第一次输一次密码。BotLan 会在 Spark 上授权本机密钥（之后免密）、记住主机指纹、建立并维持隧道，并把向导里建好的 Bot 自动加进来。
4. **让 Bot 干活**：点右上角滑轨上的 Bot 进入 Chat，比如说「检查一下 Spark 的 GPU」，在弹出的卡片里批准命令。
5. **安装技能**：Chat 菜单 →「技能广场」→ 安装 → 「在对话中试用」。
6. **多个 Bot**：在左侧栏 Spark 弹窗里的「分区管理」新建分区，滑轨上会多出一个 Bot；也可以在 Spark 上重新运行 `sh ~/spark-duo/scripts/09_setup.sh`。

其他接入方式（不用 BotLan 网关）：任意 OpenAI 兼容接口都可以手动添加为 Bot；公网网关见 [8.4](#84-dgx-vl-api-网关)。

<img width="1635" height="1198" alt="BotLan Chat workspace" src="https://github.com/user-attachments/assets/bf17ac10-e6e3-4813-b9e8-1119a99611dd" />

<img width="1503" height="833" alt="BotLan Skills" src="https://github.com/user-attachments/assets/681b3fe8-8161-4961-b212-78e6ad1fab33" />

## 8. 功能详解

### 8.1 胶囊与岛

- **Small Island**：停靠在桌面顶部，鼠标悬停展开，显示电量与 Wi-Fi 状态。
- **Long Island**：双击胶囊切换；竖向胶囊里列出所有 Bot，有消息时向左弹出气泡。没有 Bot 时显示「连接 DGX Spark」。
- **Chat 窗口**：
  - 右上角弧形滑轨固定显示 3 个位置，空位显示「待创建」，点击打开 Bot 配置；超过 3 个 Bot 时滚轮转动滑轨。
  - 拖动卡片的边或角改变卡片的真实宽高（最小 640×480，不超出屏幕，下次打开保持）。滑轨、边框和文字大小不变，页面重新排版。
  - 点 × 时滑轨沿轨道滑走，卡片在右上角收成 Long Island 胶囊。
  - 应用启动时预热浏览器内核和本地后台；页面加载时显示骨架，不会白屏。

### 8.2 Bot 动画

- `BotAnimationEngine.Sample(t, state, options)` 为纯函数采样，按时间生成动画帧，不依赖 UI 时钟；`BotView` 只负责 WPF 绘制。
- 状态序列、形态、表情、视线拟合、皮肤与装饰可自由组合；窗口关闭或隐藏时释放渲染回调。

### 8.3 Chat 工作区

- **工作流时间线**：每次回复上方列出 Jev 选技能、思考、每次工具调用、等待审批和最终回答，带图标、状态、耗时，可以展开看完整参数和结果；回复进行中自动展开。
- **底部统计**：每条回复显示输出速度、输出 token 数、上下文占用（如 `70 tok/s · 613 输出 · 上下文 3.3K / 66K · 首字 0.2s`），上下文快满时变红。
- **附件**：点 + 选文件、粘贴截图或拖进对话区。图片作为图片输入交给视觉模型（GELab-Zero-4B 能读图）；200 KB 以内的文本文件直接附进对话；限制为图片 10 MB、文件 20 MB、每条 8 个，服务端按内容判断类型，拒绝可执行文件。
- **Web 工具**：`web_search`（无需 API Key，也可配置 SearXNG / Brave）、`browse_web`、`read_more`、`extract_links`、`fetch_url`、`get_time`、`calculate`。网络访问带 SSRF 防护：只允许公网 http(s)，解析后的私网、本机、元数据地址一律拒绝，每次跳转都重新检查。
- **输出排版**：代码块带语言标签和复制按钮，表格可以横向滚动，任务列表、引用、长回复折叠；每条回复可复制、可重新生成。
- **Spark 状态**：左侧栏底部的 Spark 按钮显示内存占用环和状态点，点开是统一内存、GPU 利用率/温度/功耗、GPU 进程、最新的 Jev 路由和分区管理。
- 原有功能：会话与线程、产物、代码工作台、浏览器控制台、PDF 阅读、邮件工具卡片、后台任务、目标、网页监控、记忆、财务 CSV 分析；可选 Docker 计算容器与浏览器容器（不自动安装、不自动授权）。

### 8.4 DGX VL API 网关

把 Spark 上的本地 OpenAI 兼容服务经跳板机暴露为带 Key 管理的公网接口，支持多设备、多账号共用同一 Base URL，由 API Key 决定路由。默认对外模型名为 `qwen3-vl`，运营方可通过 `VL_MODEL` 配置。完整文档见 [`dgx-vl-api/README.md`](dgx-vl-api/README.md)。

```bash
curl https://kilee.cn/api/vl/chat/completions \
  -H "Authorization: Bearer vl_xxx" -H 'Content-Type: application/json' \
  -d '{"model":"qwen3-vl","messages":[{"role":"user","content":"你好"}],"max_tokens":1200}'
```

Thinking 类模型请将 `max_tokens` 设为 800 以上，非流式调用的客户端超时设为 300 秒以上。

## 9. 安全设计

| 面 | 措施 |
| --- | --- |
| 桌面宿主 ↔ 本地后台 | 后台只监听 `127.0.0.1` 随机端口；每次启动生成 host key，Bot 注册接口拒绝任何浏览器来源请求；模型密钥不进入网页和 URL |
| Bot 隔离 | 会话、记忆、任务、技能均按 Bot 存储；跨 Bot 运行请求返回 403（有集成测试覆盖） |
| 技能 | 只接受公开 HTTPS 来源，拒绝内网地址、带凭据 URL 和超过 256 KB 的文件；广场条目逐条结构校验；技能文本不能覆盖系统安全规则；记录 SHA-256 摘要 |
| Agent 行为 | 工具结果、邮件、网页与技能均视为不可信数据；外部写操作需在原生界面审批；Agent 不得在无证据时声称完成 |
| 计算容器 | 断网、只读根文件系统、丢弃全部 capability、`no-new-privileges`、内存与进程数上限 |
| Spark 网关 | Spark 主动外连，服务器从不连回、不持有私钥；隧道账号由 `permitlisten` 钉死单端口，sshd `Match` 块禁止 shell 与本地转发；一把公钥只能属于一个账号 |
| 推理服务 | 所有技能默认把模型服务绑定到 `127.0.0.1` 并要求 API Key，通过 SSH 隧道或网关访问 |
| 连接 DGX Spark | 密码只在第一次登录时使用，从不保存；本机生成的 SSH 私钥用 Windows DPAPI 加密存放；Spark 主机指纹首次连接时记下，之后变化就拒绝连接；隧道只监听 `127.0.0.1` |
| 在 Spark 上执行 | 每条命令都要用户在原生界面批准，模型不能自己批准；分区 Key 只交给桌面宿主（网页拿到的是一次性领取码），网关只存 SHA-256；分区命令在 systemd slice + Landlock 里运行，读不到主 Key 和 `~/.ssh` |
| Web 工具 | 只允许公网 http(s)（80/443），拒绝带凭据的 URL；DNS 解析结果有一个是私网/本机/元数据地址就整体拒绝（防 DNS rebinding），连接钉在通过检查的 IP 上；每次跳转都重新检查；1 MB、15 秒上限 |
| 附件 | 按内容判断类型，拒绝可执行文件和空文件；按 owner 隔离，内容只通过 15 分钟签名链接访问；文件内容标记为不可信数据 |

## 10. 从源码构建与测试

**环境要求**：.NET 8 SDK、Node.js 22+（打包脚本按 24 验证）、pnpm 11.7。

```powershell
pnpm --dir Chat install --frozen-lockfile
pnpm --dir Chat run build:island
dotnet build IslandUI/IslandUI.csproj
& '.\IslandUI\bin\Debug\net8.0-windows\IslandUI.exe'
```

调试版可加 `--chat` 直接打开已配置 Bot 的 Chat。只改前端时可用 `pnpm --dir Chat run dev:mobile` 在浏览器中迭代。

```powershell
pnpm --dir Chat run typecheck             # 后台与前端类型检查
pnpm --dir Chat test                       # 单元测试（含技能服务测试）
pnpm --dir Chat run test:island            # 嵌入式后台集成测试（需先 build:island）
dotnet run --project .verify-bots/BotsVerify.csproj    # Bot 动画引擎校验
dotnet run --project .verify-chat/ChatPreview.csproj   # 滑轨、拉伸、关闭、启动与 Studio 冒烟校验
dotnet run --project .verify-spark/SparkVerify.csproj -- --unit   # 连接 DGX Spark 向导的单元校验
```

单元测试覆盖：Spark 审批（未批准不执行、拒绝/中断即不执行、分区领取码只能用一次）、上传限制、SSRF 防护、搜索结果解析、计算器、速度统计事件、工作流步骤构建。集成测试会启动真实的嵌入式后台与本地模拟模型服务，验证：Bot 间会话 / 身份 / 模型 / 密钥隔离；跨 Bot 运行被拒绝；技能按 Bot 安装且只出现在该 Bot 的系统提示词中；`load_skill` 工具被提供给模型；非法技能 id 被拒绝。

### 打包安装器

需要 Inno Setup 6.7+ 与微软官方 WebView2 引导程序（脚本会校验其 Authenticode 签名必须来自 Microsoft）：

```powershell
.\Packaging\Build.ps1 -InnoCompiler 'C:\Tools\InnoSetup\ISCC.exe' `
                      -WebViewBootstrapper 'C:\Tools\MicrosoftEdgeWebview2Setup.exe'
```

隔离部署测试参数：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PORTABLE=1 /DIR="测试目录"`。详见 [`Packaging/README.md`](Packaging/README.md)。

## 11. 目录结构

```
IslandUI/            C#/WPF 桌面宿主：胶囊、Long Island、Chat 窗口、Bot 绘制与系统状态
  Bots/              Bot 动画引擎（采样、状态、形态、表情、皮肤）
  Controls/          自定义控件（弧形滑轨标签、弹性动画、像素加载等）
  Services/          系统状态、Bot 配置存储、Chat 运行时、连接 DGX Spark（SSH、隧道、配对）
Chat/                pnpm 工作区：嵌入页面 + 本地后台
  apps/mobile/       Expo / React Native Web 界面（含技能页 skills-screen.tsx）
  apps/server/       Hono 本地服务
    src/skills.ts          技能服务：安装、校验、SKILL.md 解析、load_skill 工具
    src/skills-catalog.ts  内置 DGX Spark / StepFun 技能与 NVIDIA 官方技能目录
    src/engine/            Agent 运行时（对话、后台任务、模型适配、速度统计）
    src/spark.ts           Spark 网关客户端：审批队列、遥测、分区
    src/web-tools.ts       Web 工具与 SSRF 防护
    src/uploads.ts         附件上传
  apps/worker/       可选浏览器容器
  apps/computer/     可选 Docker 计算容器
  packages/          domain / backends / integrations 共享包
  tests/             集成测试与技能测试
dgx-vl-api/          DGX Spark → 公网 OpenAI 兼容 API 网关与控制台
Packaging/           发布构建与 Inno Setup 安装器脚本
.verify-bots/        Bot 动画引擎校验程序
.verify-chat/        滑轨、拉伸、关闭、启动与 Studio 冒烟校验程序
.verify-spark/       连接 DGX Spark 向导的单元与实机校验程序
licenses/            第三方许可全文
docs/                交付说明、审查记录与源码校验清单
```

## 12. 数据与隐私

| 场景 | 位置 |
| --- | --- |
| 安装版用户数据（含已安装技能） | `%LOCALAPPDATA%\IslandUI` |
| 源码版 Chat 数据 | `Chat/.island-data` |

以上目录不应提交或分发，已在 `.gitignore` 中排除；卸载程序默认保留用户数据。本仓库不包含 `.env`、真实 API 密钥、聊天数据库、WebView 用户目录、`node_modules` 或编译产物。历史上使用过的密钥建议在服务商后台撤销或轮换。

## 13. 已知限制

- 安装器没有发布者代码签名，Windows 可能提示未知发布者；`SHA256SUMS` 只校验传输完整性。
- Spark 上的命令只有经用户逐条批准后才会执行；命令以普通用户运行，没有 sudo。
- 分区限制 CPU 侧内存、CPU 和进程数（systemd slice），**不限制 GPU 显存**（CUDA 分配不计入 cgroup）。Landlock 限制文件访问范围，**不限制网络**。所有分区共用一个 Unix 用户：这是资源划分，不是 Bot 之间的安全隔离。
- 演示主线的推理引擎是 llama.cpp（CUDA 后端）。TensorRT-LLM、NIM、vLLM 以部署技能的形式提供，演示里没有运行它们。
- GELab-Zero-4B 是 4B 小模型，不是推理模型：偶尔会只列步骤而不调用工具，重问一次通常可以；读图里的文字偶尔会漏字。
- 如果本机代理使用 fake-IP DNS（如 198.18.x.x），SSRF 防护会拦下 `web_search` 和 `fetch_url`；请把代理改成 real-IP / redir-host 模式，或配置 `SEARXNG_URL`。
- PDF 附件暂时读不出正文，只作为文件引用交给已有的 PDF 工具。
- BotLan 在任务管理器里被强制结束时，本地 PGlite 数据库可能损坏；请从托盘菜单「退出」。
- StepFun Step-3.7-Flash / Step3-VL-10B 的性能数字引自官方模型卡；Jev + GELab 的数字是在一台 DGX Spark 上实测的（见 BotLan-CLI）。
- 没有在缺少 WebView2 的全新离线电脑、多显示器混合 DPI 环境下验证。

## 14. 第三方与许可

本项目以 MIT 许可发布，见 [LICENSE](LICENSE)。

- `IslandUI/Resources/MapleMono-CN-*.ttf` 为 Maple Mono 字体，使用 SIL Open Font License 1.1。
- NVIDIA 官方技能内容在安装时从上游获取，许可以上游仓库为准（代码 Apache-2.0，文档与技能 CC-BY-4.0）。
- StepFun Step-3.7-Flash 与 Step3-VL-10B 模型权重为 Apache-2.0，由用户自行从 Hugging Face 下载，不随本项目分发。
- .NET、WebView2、Node.js 及 JavaScript 依赖遵循各自许可，完整声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 [`licenses/`](licenses/)。

---

## English

**BotLan** is a Windows desktop "dynamic island" agent client for the NVIDIA DGX Spark Hackathon (finalist). One DGX Spark hosts many Bots; you switch between them on a curved rail at the corner of the Chat window.

**On the Spark** ([BotLan-CLI](https://github.com/Yunle-Lee/BotLan-CLI)): one command (`curl -fsSL https://raw.githubusercontent.com/Yunle-Lee/BotLan-CLI/main/install.sh | sh`) installs a prebuilt CUDA sm_121 llama.cpp and two models from ModelScope: Jev-Style-0.8B, a discriminative router that never generates (~28 ms), and StepFun GELab-Zero-4B, a vision-language model (~76 tok/s measured). Both stay resident in GB10's unified memory. The BotLan gateway routes each request to a skill with Jev, lets the 4B call `run_spark_command`, and runs a command only after the user approves it in BotLan. Each Bot is a zone: a systemd slice (memory, CPU, tasks) plus Landlock file scope.

**On the laptop**: "Connect DGX Spark" logs in over SSH once, installs its own key (stored with DPAPI), pins the host key and keeps the tunnel up. Chat shows a step-by-step workflow timeline, per-reply speed and context usage, approval cards, image and file attachments, and web tools with SSRF protection.

**Agent Skills** use the same SKILL.md format as `github.com/NVIDIA/skills`. They are installed per Bot and loaded progressively with `load_skill`. Skill → Jev picks it → the Agent runs it → the user approves → the Spark executes it in the Bot's zone.

**Honest limits**: the demo engine is llama.cpp (TensorRT-LLM / NIM are deployment skills only). Zones do not cap GPU memory and Landlock does not restrict network access. The 4B model sometimes lists steps instead of calling a tool.

**Build**: .NET 8 SDK, Node.js 22+, pnpm 11.7. `pnpm --dir Chat install --frozen-lockfile`, `pnpm --dir Chat run build:island`, `dotnet build IslandUI/IslandUI.csproj`. Tests: `pnpm --dir Chat test`, `pnpm --dir Chat run test:island`.

MIT licensed. Maple Mono is under SIL OFL 1.1. NVIDIA skills and StepFun weights keep their upstream licenses.
