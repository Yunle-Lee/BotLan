// Built-in skills shipped with BotLan. Each entry follows the Agent Skills layout
// (name + description metadata, Markdown instructions loaded on demand).
// NVIDIA entries are fetched from github.com/NVIDIA/skills at install time so the
// Agent always receives the upstream, signed-catalog SKILL.md rather than a copy.

export type SkillSourceKind = "builtin" | "nvidia" | "market" | "url";

export interface SkillManifest {
  id: string;
  name: string;
  version: string;
  author: string;
  category: string;
  icon: string;
  tags: string[];
  summary: string;
  description: string;
  /** Markdown body given to the Agent by load_skill. Empty until fetched for remote sources. */
  instructions: string;
  source: { kind: SkillSourceKind; url?: string };
  /** A starter prompt the UI can send to chat to exercise the skill. */
  tryPrompt?: string;
  commands?: { label: string; command: string; language?: string }[];
  requiresEnv?: { key: string; label: string; required: boolean }[];
  featured?: boolean;
}

const SPARK = "DGX Spark";
const STEPFUN = "StepFun 阶跃星辰";
const NVIDIA = "NVIDIA 官方";

const sparkFacts = `DGX Spark facts you can rely on: one NVIDIA GB10 Grace Blackwell Superchip, 128 GB of CPU–GPU coherent unified memory, DGX OS (Ubuntu-based, aarch64), Docker with the NVIDIA Container Toolkit, and native FP4 (NVFP4) support on the Blackwell GPU. Because memory is unified, "fits in VRAM" means "fits in 128 GB minus what the OS and other processes use"; plan for roughly 110–115 GB usable for weights plus KV cache.`;

const execRules = `How to execute: if the run_spark_command tool is available, this Bot is connected to the DGX Spark through the BotLan gateway — run each command yourself with it, one command per call with a short reason, and the person approves every command in BotLan before it runs. Read the returned output before the next step. If a command is denied, stop and ask. Otherwise (no run_spark_command), BotLan's private computer is an isolated, network-disabled container without GPU access, so you cannot run these commands on the DGX Spark yourself: give the user exact, copy-ready commands for their Spark terminal (or NVIDIA Sync), explain what each step does, and ask them to paste back the output. Never claim a command ran or a model is serving unless the user's pasted output or a tool result shows it. Bind servers to 127.0.0.1 and reach them through an SSH tunnel or the BotLan VL gateway; never suggest exposing an unauthenticated port to a LAN or the internet.`;

export const builtinSkills: SkillManifest[] = [
  {
    id: "spark-model-planner",
    name: "DGX Spark 模型选型规划",
    version: "1.0.0",
    author: "BotLan",
    category: SPARK,
    icon: "Cpu",
    tags: ["DGX Spark", "128GB", "NVFP4", "StepFun", "选型"],
    summary: "按 128 GB 统一内存精确估算权重与 KV Cache，为每台 Spark 选择模型、精度与推理引擎。",
    description:
      "把 DGX Spark 的核心优势（128 GB CPU-GPU 统一内存 + Blackwell FP4）转化为可执行的部署决策：给出显存账本、推荐引擎（TensorRT-LLM / vLLM / SGLang / llama.cpp / NIM）以及对应的部署技能。",
    tryPrompt: "我有一台 DGX Spark，想同时跑一个多模态模型和一个大的推理模型，帮我做内存规划。",
    featured: true,
    source: { kind: "builtin" },
    instructions: `# DGX Spark model planner

Use when the user asks which model, precision or engine to run on one or more DGX Spark nodes.

${sparkFacts}

## Method
1. Ask for the goal (chat, coding, vision, voice, RAG), the number of Sparks, and the context length they need.
2. Write a memory ledger: weights (params × bytes per param: BF16 = 2, FP8 = 1, NVFP4/Q4 ≈ 0.5–0.6) + vision projector + KV cache + about 10 GB headroom. Show the arithmetic.
3. Pick an engine:
   - **TensorRT-LLM** (container \`nvcr.io/nvidia/tensorrt-llm/release\`, OpenAI API on port 8355): best throughput for NVIDIA-published FP8/NVFP4 checkpoints such as \`nvidia/Qwen3-30B-A3B-FP4\` or \`nvidia/NVIDIA-Nemotron-3-Super-120B-A12B-NVFP4\`. Skill: \`spark-trtllm-serve\`.
   - **NIM** (\`nvcr.io/nim/...-dgx-spark\`, port 8000): pre-optimized, lowest effort. Skill: \`spark-nim-deploy\`.
   - **vLLM / SGLang**: newest architectures and day-0 models, e.g. StepFun Step3-VL-10B. Skill: \`stepfun-step3-vl-spark\`.
   - **llama.cpp (GGUF)**: largest models on a single Spark via 3–4-bit quants, e.g. StepFun Step-3.7-Flash IQ4_XS. Skill: \`stepfun-step37-flash-spark\`.
4. Reference sizes (weights only, from the model cards):
   | Model | Format | Size | Single Spark? |
   |---|---|---|---|
   | StepFun Step3-VL-10B | BF16 | ~20 GB (+~4 GB runtime) | Yes, with room for a second model |
   | StepFun Step-3.7-Flash (198B MoE, ~11B active) | GGUF IQ4_XS + mmproj | ~105 GB + 4 GB | Yes, full 256K context per StepFun's Spark benchmark |
   | StepFun Step-3.7-Flash | GGUF Q4_K_S + mmproj | ~112 GB + 4 GB | Yes, tested to ~131K context |
   | StepFun Step-3.7-Flash | NVFP4 (ModelOpt) | ~104B params stored | Card examples use 4-way tensor parallel; not a single-Spark target |
   | StepFun Step-3.7-Flash | BF16 / FP8 | ~200 GB+ | No |
5. For two Sparks connected by QSFP, mention NVIDIA's "connect two Sparks" playbook and that BotLan treats each Spark as one Bot.
6. End with the recommended skill to install next and the exact Bot settings (Base URL, model name).

Label estimates as estimates. Do not invent benchmark numbers; quote only figures from model cards or the user's own measurements.

${execRules}`,
  },
  {
    id: "stepfun-step37-flash-spark",
    name: "StepFun Step-3.7-Flash 单机部署",
    version: "1.0.0",
    author: "BotLan × StepFun",
    category: STEPFUN,
    icon: "Sparkles",
    tags: ["StepFun", "Step-3.7-Flash", "llama.cpp", "GGUF", "256K"],
    summary:
      "在单台 DGX Spark 上以 llama.cpp + IQ4_XS 量化运行阶跃星辰 198B MoE 多模态旗舰模型，接入 BotLan。",
    description:
      "Step-3.7-Flash 是阶跃星辰开源（Apache-2.0）的稀疏 MoE 视觉语言模型，总参数约 198B，每 token 激活约 11B，原生 256K 上下文。IQ4_XS 量化约 105 GB，正好落在 DGX Spark 128 GB 统一内存之内，是“单机跑旗舰”的最佳示范。",
    tryPrompt: "帮我在 DGX Spark 上部署 Step-3.7-Flash，并接入 BotLan 的 Bot。",
    featured: true,
    source: { kind: "builtin" },
    commands: [
      {
        label: "编译 StepFun llama.cpp 分支（CUDA）",
        command:
          "git clone https://github.com/stepfun-ai/llama.cpp.git && cd llama.cpp && git checkout -b step3.7 origin/step3.7 && cmake -B build -DGGML_CUDA=ON -DLLAMA_BUILD_SERVER=ON && cmake --build build --config Release -j$(nproc)",
        language: "bash",
      },
      {
        label: "启动 OpenAI 兼容服务（仅本机）",
        command:
          './build/bin/llama-server -m ~/models/step37/Step-3.7-flash-IQ4_XS.gguf --mmproj ~/models/step37/mmproj-Step-3.7-flash-f16.gguf --alias step-3.7-flash -c 131072 -ngl 99 -fa on --host 127.0.0.1 --port 8080 --api-key "$BOTLAN_LOCAL_KEY"',
        language: "bash",
      },
    ],
    instructions: `# StepFun Step-3.7-Flash on one DGX Spark

Use when the user wants StepFun's flagship open model running locally on a DGX Spark.

${sparkFacts}

## Model facts (from the Hugging Face model card)
- \`stepfun-ai/Step-3.7-Flash\`: sparse MoE vision-language model, ~198B total parameters (196B language + 1.8B vision encoder), ~11B active per token, 256K context, three reasoning levels, Apache-2.0.
- GGUF quants in \`stepfun-ai/Step-3.7-Flash-GGUF\`: IQ4_XS ≈ 105 GB, Q4_K_S ≈ 112 GB, Q3_K_L ≈ 103 GB, Q3_K_M ≈ 94 GB, IQ3_XXS ≈ 76 GB; vision projector \`mmproj\` (f16) ≈ 4 GB.
- StepFun's DGX Spark benchmark ran IQ4_XS at 262K context (~24 tok/s generation near empty context). Q4_K_S was only shown to ~131K. Treat speed numbers as the vendor's, not BotLan's.
- GGUF support lives on StepFun's llama.cpp fork, branch \`step3.7\`.

## Steps (on the Spark)
1. Check free memory: \`free -h\` and stop other large model servers first.
2. Download: \`hf download stepfun-ai/Step-3.7-Flash-GGUF --include "*IQ4_XS*" "mmproj*f16*" --local-dir ~/models/step37\` (the user needs \`huggingface_hub\` CLI; list files with \`ls -lh ~/models/step37\` and use the exact names).
3. Build with CUDA:
   \`\`\`bash
   git clone https://github.com/stepfun-ai/llama.cpp.git && cd llama.cpp
   git checkout -b step3.7 origin/step3.7
   cmake -B build -DGGML_CUDA=ON -DLLAMA_BUILD_SERVER=ON
   cmake --build build --config Release -j$(nproc)
   \`\`\`
4. Serve, bound to localhost and protected by a key:
   \`\`\`bash
   export BOTLAN_LOCAL_KEY=$(openssl rand -hex 24)
   ./build/bin/llama-server -m ~/models/step37/<IQ4_XS file>.gguf \\
     --mmproj ~/models/step37/<mmproj file>.gguf --alias step-3.7-flash \\
     -c 131072 -ngl 99 -fa on --host 127.0.0.1 --port 8080 --api-key "$BOTLAN_LOCAL_KEY"
   \`\`\`
   Start at \`-c 32768\` if memory is tight; raise toward 262144 only with IQ4_XS or smaller.
5. Verify: \`curl -s http://127.0.0.1:8080/v1/models -H "Authorization: Bearer $BOTLAN_LOCAL_KEY"\`.
6. Connect BotLan, either:
   - SSH tunnel from the Windows PC: \`ssh -N -L 8080:127.0.0.1:8080 <user>@<spark-host>\`, then Bot Base URL \`http://127.0.0.1:8080/v1\`, model \`step-3.7-flash\`, API key = \`$BOTLAN_LOCAL_KEY\`; or
   - the BotLan VL gateway (skill \`botlan-spark-gateway\`) after pointing the gateway's upstream at this port.

## Troubleshooting
- Out of memory at load: use a smaller quant or lower \`-c\`.
- Model returns only reasoning: raise \`max_tokens\` (≥ 1024) in the client.
- Build cannot find CUDA: confirm \`nvcc --version\` and that the CUDA toolkit is on PATH.

${execRules}`,
  },
  {
    id: "stepfun-step3-vl-spark",
    name: "StepFun Step3-VL-10B 视觉推理",
    version: "1.0.0",
    author: "BotLan × StepFun",
    category: STEPFUN,
    icon: "Scan",
    tags: ["StepFun", "Step3-VL", "vLLM", "多模态", "GUI Grounding"],
    summary:
      "用 vLLM 在 DGX Spark 上部署阶跃星辰 10B 视觉语言模型，为 Bot 提供看图、OCR 与界面理解能力。",
    description:
      "Step3-VL-10B（Apache-2.0）在 OCR、STEM 推理与 GUI Grounding 上表现突出，BF16 约 24 GB，可与大语言模型同机共存，充分利用 Spark 的统一内存。",
    tryPrompt: "帮我在 DGX Spark 上用 vLLM 部署 Step3-VL-10B，作为 BotLan 的看图 Bot。",
    featured: true,
    source: { kind: "builtin" },
    commands: [
      {
        label: "启动 vLLM（仅本机）",
        command:
          'vllm serve stepfun-ai/Step3-VL-10B --served-model-name step3-vl --trust-remote-code --reasoning-parser deepseek_r1 --enable-auto-tool-choice --tool-call-parser hermes --host 127.0.0.1 --port 8000 --api-key "$BOTLAN_LOCAL_KEY"',
        language: "bash",
      },
    ],
    instructions: `# StepFun Step3-VL-10B on DGX Spark (vLLM)

Use when the user wants an image-understanding Bot (screenshots, documents, charts, GUI grounding) served from their Spark.

${sparkFacts}

## Model facts (from the Hugging Face model card)
- \`stepfun-ai/Step3-VL-10B\`: 10B parameters (1.8B PE-lang vision encoder + Qwen3-8B decoder), BF16 weights ≈ 20 GB, ~24 GB with runtime overhead. An FP8 variant exists (\`stepfun-ai/Step3-VL-10B-FP8\`). Apache-2.0.
- The card states BF16 inference is the supported path and \`--trust-remote-code\` is mandatory. It requires a recent vLLM (≥ 0.14 nightly).
- Strong at OCR (OCRBench 86.75), MMMU 78.11 and ScreenSpot-V2 92.61 per the card.

## Steps (on the Spark)
1. Use the vLLM container recommended by NVIDIA's DGX Spark vLLM playbook (build.nvidia.com/spark/vllm). If its vLLM is older than the card requires, install the nightly inside the container: \`pip install -U vllm --pre --extra-index-url https://wheels.vllm.ai/nightly\`.
2. Serve:
   \`\`\`bash
   export BOTLAN_LOCAL_KEY=$(openssl rand -hex 24)
   vllm serve stepfun-ai/Step3-VL-10B --served-model-name step3-vl --trust-remote-code \\
     --reasoning-parser deepseek_r1 --enable-auto-tool-choice --tool-call-parser hermes \\
     --gpu-memory-utilization 0.3 --host 127.0.0.1 --port 8000 --api-key "$BOTLAN_LOCAL_KEY"
   \`\`\`
   \`--gpu-memory-utilization 0.3\` leaves unified memory for a second model; raise it if this is the only server.
3. Verify with \`curl -s http://127.0.0.1:8000/v1/models -H "Authorization: Bearer $BOTLAN_LOCAL_KEY"\`.
4. Connect a Bot: SSH tunnel \`ssh -N -L 8000:127.0.0.1:8000 <user>@<spark-host>\`, Base URL \`http://127.0.0.1:8000/v1\`, model \`step3-vl\`.
5. If generation never stops, the card points to an \`eos_token_id\` fix in the latest model revision; re-download.

${execRules}`,
  },
  {
    id: "spark-trtllm-serve",
    name: "TensorRT-LLM NVFP4 推理服务",
    version: "1.0.0",
    author: "BotLan · NVIDIA Playbook",
    category: SPARK,
    icon: "Layers",
    tags: ["TensorRT-LLM", "NVFP4", "Blackwell", "OpenAI API"],
    summary:
      "按 NVIDIA DGX Spark Playbook 用 TensorRT-LLM 容器发布 FP8/NVFP4 模型的 OpenAI 兼容接口。",
    description:
      "TensorRT-LLM 是 NVIDIA 在 Blackwell 上吞吐最高的推理栈。本技能指导 Agent 带用户完成镜像拉取、GPU 校验、trtllm-serve 启动与 BotLan 接入。",
    tryPrompt: "用 TensorRT-LLM 在我的 DGX Spark 上跑一个 NVFP4 模型并接到 BotLan。",
    source: { kind: "builtin" },
    commands: [
      {
        label: "校验 GPU 容器",
        command:
          "docker run --rm --gpus all nvcr.io/nvidia/tensorrt-llm/release:1.3.0rc13 nvidia-smi",
        language: "bash",
      },
    ],
    instructions: `# TensorRT-LLM on DGX Spark

Follows NVIDIA's DGX Spark TensorRT-LLM playbook (build.nvidia.com/spark/trt-llm). Defer to that page for flags that change between releases.

${sparkFacts}

## Steps
1. Prerequisites: \`nvidia-smi\`; \`echo $HF_TOKEN\` (needed for gated models).
2. Validate GPU access in the image: \`docker run --rm --gpus all nvcr.io/nvidia/tensorrt-llm/release:1.3.0rc13 nvidia-smi\`.
3. Choose an NVIDIA-optimized checkpoint that fits one Spark, for example \`nvidia/Qwen3-8B-FP8\`, \`nvidia/Qwen3-30B-A3B-FP4\`, \`nvidia/Llama-3.3-70B-Instruct-FP4\` or \`nvidia/NVIDIA-Nemotron-3-Super-120B-A12B-NVFP4\`. \`nvidia/Qwen3-235B-A22B-FP4\` requires two Sparks.
4. Serve (playbook port 8355 for LLMs, 8356 for VLMs), bound to localhost:
   \`\`\`bash
   docker run --rm -it --gpus all --ipc host --ulimit memlock=-1 --ulimit stack=67108864 \\
     -p 127.0.0.1:8355:8355 -e HF_TOKEN -v $HOME/.cache/huggingface:/root/.cache/huggingface \\
     nvcr.io/nvidia/tensorrt-llm/release:1.3.0rc13 \\
     trtllm-serve nvidia/Qwen3-30B-A3B-FP4 --host 0.0.0.0 --port 8355
   \`\`\`
   (Inside the container 0.0.0.0 is fine; the \`-p 127.0.0.1:\` mapping keeps it off the network.)
5. Verify: \`curl -s http://127.0.0.1:8355/v1/models\`.
6. BotLan Bot: SSH tunnel port 8355, Base URL \`http://127.0.0.1:8355/v1\`, model name as returned by \`/v1/models\`. trtllm-serve has no API key by default, so keep it on localhost.

Rollback: stop the container and delete unused models from \`~/.cache/huggingface\`.

${execRules}`,
  },
  {
    id: "spark-nim-deploy",
    name: "NVIDIA NIM 一键推理微服务",
    version: "1.0.0",
    author: "BotLan · NVIDIA Playbook",
    category: SPARK,
    icon: "Database",
    tags: ["NIM", "NGC", "DGX Spark", "OpenAI API"],
    summary: "拉取 DGX Spark 专用 NIM 容器，几分钟内得到经过优化的 OpenAI 兼容 LLM 服务。",
    description:
      "NIM 把模型、推理引擎与优化配置打包为容器。适合想要“开箱即用”的用户，也是 BotLan 农场中标准化节点的推荐方案。",
    tryPrompt: "帮我在 DGX Spark 上部署 NVIDIA NIM 并让 BotLan 使用它。",
    source: { kind: "builtin" },
    requiresEnv: [
      { key: "NGC_API_KEY", label: "NGC API Key (ngc.nvidia.com/setup/api-key)", required: true },
    ],
    instructions: `# NVIDIA NIM on DGX Spark

Follows NVIDIA's DGX Spark NIM playbook (build.nvidia.com/spark/nim-llm).

${sparkFacts}

## Steps
1. Create an NGC API key at ngc.nvidia.com/setup/api-key and export it on the Spark as \`NGC_API_KEY\`. Never paste the key into chat; tell the user to type it in their own terminal.
2. Log in: \`echo "$NGC_API_KEY" | docker login nvcr.io -u '$oauthtoken' --password-stdin\`.
3. Run the Spark build of the default NIM, bound to localhost:
   \`\`\`bash
   mkdir -p ~/.cache/nim
   docker run -it --rm --gpus all --shm-size=16GB -e NGC_API_KEY \\
     -v ~/.cache/nim:/opt/nim/.cache -p 127.0.0.1:8000:8000 \\
     nvcr.io/nim/meta/llama-3.1-8b-instruct-dgx-spark:latest
   \`\`\`
   The playbook also lists a Qwen3-32B NIM for Spark.
4. Verify: \`curl -s http://127.0.0.1:8000/v1/models\`.
5. Connect a Bot through an SSH tunnel on port 8000 with the model id returned by \`/v1/models\`.

Rollback: \`docker stop <container>\`. Model weights stay in \`~/.cache/nim\`; delete only to reclaim disk.

${execRules}`,
  },
  {
    id: "spark-node-health",
    name: "DGX Spark 节点体检",
    version: "1.0.0",
    author: "BotLan",
    category: SPARK,
    icon: "ShieldCheck",
    tags: ["DGX Spark", "诊断", "nvidia-smi", "农场运维"],
    summary:
      "一套标准化体检清单：驱动、GPU 容器、统一内存、磁盘、端口与推理服务存活，适合 Spark 农场巡检。",
    description:
      "让每个 Bot 都能为自己的 Spark 节点出具体检报告。Agent 会给出命令、解读输出，并可把巡检设为 BotLan 定时任务。",
    tryPrompt: "给我的 DGX Spark 做一次体检，告诉我要执行哪些命令。",
    source: { kind: "builtin" },
    instructions: `# DGX Spark node health check

Use for "is my Spark OK", slow inference, failed model loads, or routine farm inspection.

${sparkFacts}

## Checklist (run each with run_spark_command when available; otherwise ask the user to run and paste output)
\`\`\`bash
nvidia-smi                                   # driver, GPU, running processes
free -h                                      # unified memory headroom
df -h ~ /var/lib/docker                      # model cache and image space
docker ps --format '{{.Names}}\\t{{.Image}}\\t{{.Status}}\\t{{.Ports}}'
docker run --rm --gpus all nvcr.io/nvidia/cuda:13.0.1-devel-ubuntu24.04 nvidia-smi   # container GPU path
ss -ltnp | grep -E ':(8000|8080|8355|8356|30000)\\b'                                   # inference ports
\`\`\`
## How to read it
- On GB10, \`nvidia-smi\` may not report a separate framebuffer total because memory is unified; use \`free -h\` for headroom.
- Any model server listening on 0.0.0.0 without an API key is a finding: recommend 127.0.0.1 binding plus SSH tunnel or the BotLan gateway.
- If the container GPU check fails, the NVIDIA Container Toolkit or Docker group membership is the usual cause.
- Report: status (healthy / degraded / down), evidence lines, and the single next action.
- Offer to save a recurring check with BotLan's tasks so the farm is inspected on a schedule.

${execRules}`,
  },
  {
    id: "botlan-spark-gateway",
    name: "BotLan Spark 网关配对",
    version: "1.0.0",
    author: "BotLan",
    category: SPARK,
    icon: "Route",
    tags: ["DGX Spark", "反向隧道", "多租户", "API Key"],
    summary:
      "通过 kilee.cn/vl 控制台把 Spark 本地模型安全地变成带 Key 管理的公网 OpenAI 兼容接口。",
    description:
      "BotLan 自研的 DGX VL API：Spark 主动向跳板机建立受限 SSH 反向隧道，服务器从不主动连回、不持有私钥；按 API Key 路由到对应设备，实现“一台 Spark = 一个 Bot”。",
    tryPrompt: "教我把 DGX Spark 配对到 BotLan 网关，然后新建一个 Bot 使用它。",
    source: { kind: "builtin" },
    instructions: `# Pair a DGX Spark with the BotLan gateway

## Flow
1. On the user's PC, open https://kilee.cn/vl/ and sign up or sign in.
2. Click "添加设备" (Add device). The console shows a one-line pairing command.
3. Run that command on the Spark as root. It downloads \`agent.sh\`, generates an SSH key pair locally (the private key never leaves the Spark), registers the public key, and installs a systemd-managed reverse tunnel that only forwards one loopback port.
4. Back in the console, wait for the device to show online, then create an API key bound to that device. Keys can be rotated, revoked and given a daily quota.
5. In BotLan Long Island, add a Bot with Base URL \`https://kilee.cn/api/vl\`, the model name shown in the console (default \`qwen3-vl\`; operators can change it with the gateway's \`VL_MODEL\` setting), and the new API key.

## Security notes to share
- Outbound only: the server never initiates connections to the Spark.
- The tunnel account is pinned to a single port with \`permitlisten\`, and sshd \`Match\` rules block shell and local forwarding.
- One public key can belong to only one account (\`409 pubkey_in_use\`); device count per account is limited (\`409 device_limit\`).
- Errors: \`401 invalid_api_key\`, \`403 account_disabled\`, \`429 rate_limit_error\`, \`503 upstream_unavailable\` (the Spark or its model server is offline).

## Thinking models
Set \`max_tokens\` ≥ 800 so reasoning does not consume the whole budget, and client timeouts ≥ 300 s for non-streaming calls.

${execRules}`,
  },
];

// Upstream NVIDIA skills. Instructions are fetched from GitHub at install time.
const nvidiaRaw = (name: string) =>
  `https://raw.githubusercontent.com/NVIDIA/skills/main/skills/${name}/SKILL.md`;
const nvidia = (
  name: string,
  title: string,
  icon: string,
  tags: string[],
  summary: string,
  tryPrompt: string,
): SkillManifest => ({
  id: `nvidia-${name}`,
  name: title,
  version: "upstream",
  author: "NVIDIA",
  category: NVIDIA,
  icon,
  tags: ["NVIDIA", ...tags],
  summary,
  description: `来自 github.com/NVIDIA/skills 的官方技能（${name}）。安装时实时拉取上游 SKILL.md，遵循 Agent Skills 规范，许可以上游为准。`,
  instructions: "",
  source: { kind: "nvidia", url: nvidiaRaw(name) },
  tryPrompt,
});

export const nvidiaSkills: SkillManifest[] = [
  nvidia(
    "rag-blueprint",
    "NVIDIA RAG Blueprint",
    "Layers",
    ["RAG", "Blueprint"],
    "部署、配置与排障 NVIDIA 企业级 RAG 蓝图（Agentic RAG、VLM、Guardrails 等）。",
    "我想在 DGX Spark 上搭 NVIDIA RAG Blueprint，按步骤带我做。",
  ),
  nvidia(
    "nemo-retriever",
    "NeMo Retriever",
    "Search",
    ["NeMo", "检索"],
    "基于 NeMo Retriever 的文档抽取、向量化与语义检索。",
    "用 NeMo Retriever 给我的 PDF 资料建一个检索服务。",
  ),
  nvidia(
    "nemotron-voice-agent-builder",
    "Nemotron 语音智能体",
    "Sparkles",
    ["Nemotron", "ASR", "TTS"],
    "用 Nemotron 语音栈组装 ASR → LLM → TTS 的实时语音 Agent。",
    "帮我规划一个 Nemotron 语音助手。",
  ),
  nvidia(
    "dynamo-troubleshoot",
    "Dynamo 推理排障",
    "Wrench",
    ["Dynamo", "Serving"],
    "诊断 NVIDIA Dynamo 分布式推理部署中的常见故障。",
    "我的 Dynamo 推理服务起不来，帮我排查。",
  ),
  nvidia(
    "cuopt-install",
    "cuOpt 安装与校验",
    "Route",
    ["cuOpt", "优化"],
    "通过 pip / conda / Docker 安装 GPU 加速优化器 cuOpt 并完成校验。",
    "在 DGX Spark 上安装 cuOpt。",
  ),
  nvidia(
    "accelerated-computing-cudf",
    "cuDF GPU 数据分析",
    "Database",
    ["RAPIDS", "cuDF", "pandas"],
    "用 RAPIDS cuDF 把 pandas 工作流迁移到 GPU 加速。",
    "把我的 pandas 脚本改成 cuDF 加速版本。",
  ),
  nvidia(
    "portfolio-optimization",
    "GPU 投资组合优化",
    "ListChecks",
    ["cuOpt", "金融"],
    "基于 NVIDIA 加速库的投资组合优化工作流。",
    "用 GPU 做一个投资组合优化示例。",
  ),
  nvidia(
    "nemoclaw-user-guide",
    "NemoClaw 使用指南",
    "Users",
    ["NemoClaw", "Agent"],
    "NVIDIA NemoClaw 智能体的安装与使用指南。",
    "介绍一下 NemoClaw，怎么在 Spark 上用？",
  ),
];
