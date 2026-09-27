import "./config.ts";
import { HttpAgent } from "@ag-ui/client";
import {
  type AgentsFactory,
  CopilotRuntime,
  InMemoryAgentRunner,
  createCopilotHonoHandler,
} from "@copilotkit/runtime/v2";
import type { Auth } from "./auth.ts";
import { configForOwner, type Config } from "./config.ts";
import { ConversationAgent } from "./engine/conversation.ts";
import type { AgentService } from "./engine/service.ts";

export function agentConfigured(config: Config) {
  return (
    Boolean(config.islandBot) ||
    config.agentBackend === "sample" ||
    (config.agentBackend === "agui"
      ? Boolean(config.agentUrl)
      : Boolean(
          config.model &&
            (process.env.OPENAI_API_KEY ||
              process.env.DEEPSEEK_API_KEY ||
              process.env.ANTHROPIC_API_KEY ||
              process.env.GOOGLE_API_KEY),
        ))
  );
}
export function makeRuntime(
  config: Config,
  service: AgentService,
  auth: Auth,
) {
  const agents: AgentsFactory = async ({ request }) => {
    const owner = await auth.owner(request.headers.get("authorization") ?? undefined);
    const selectedConfig = configForOwner(config, owner);
    return ({
    default:
      config.agentBackend === "sample"
        ? new ConversationAgent(
            selectedConfig,
            service,
            owner,
          )
        : config.agentBackend === "agui"
          ? new HttpAgent({
              url: config.agentUrl ?? "http://127.0.0.1:1/unconfigured",
              headers: config.agentToken ? { Authorization: `Bearer ${config.agentToken}` } : {},
            })
          : new ConversationAgent(
              selectedConfig,
              service,
              owner,
            ),
  });
  };
  const runtime = new CopilotRuntime({
    agents,
    // Durable conversations remain in our owner-scoped database. The SSE runner
    // only needs a bounded buffer for live/recent events, not Intelligence mode.
    runner: new InMemoryAgentRunner({ maxThreads: 128, maxRunsPerThread: 8, maxBytes: 64 * 1024 * 1024 }),
  });
  return createCopilotHonoHandler({ runtime, basePath: "/api/copilotkit" });
}
