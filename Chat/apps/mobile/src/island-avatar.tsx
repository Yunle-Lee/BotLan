import { useEffect } from "react";
import { View } from "react-native";
import { notifyIsland, useIslandBot } from "./island-host";
import { Mascot } from "./ui";

/** A layout slot for the existing native BotView, not a second animation engine. */
export function IslandAvatar({ size, covered = false }: { size: number; covered?: boolean }) {
  const bot = useIslandBot();
  useEffect(() => {
    if (!bot || typeof document === "undefined") return;
    const slot = document.getElementById("island-bot-avatar");
    if (!slot) return;
    let frame = 0;
    let previous = "";
    const report = () => {
      frame = 0;
      const rect = slot.getBoundingClientRect();
      const top = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
      const message = {
        type: "bot-avatar-bounds", botId: bot.botId,
        visible: (!covered || document.documentElement.dataset.islandFolded === "true")
          && !document.hidden && rect.width > 0 && rect.height > 0
          && !!top && (top === slot || slot.contains(top)),
        x: rect.x, y: rect.y, width: rect.width, height: rect.height,
        viewportWidth: window.innerWidth, viewportHeight: window.innerHeight,
      };
      const key = JSON.stringify(message);
      if (key !== previous) { previous = key; notifyIsland(message); }
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(report); };
    const resize = new ResizeObserver(schedule);
    resize.observe(slot);
    resize.observe(document.documentElement);
    // Sheets/toasts may obscure the slot without resizing it. No continuous polling.
    const mutations = new MutationObserver(schedule);
    mutations.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["style", "hidden"] });
    document.addEventListener("scroll", schedule, true);
    document.addEventListener("visibilitychange", schedule);
    window.addEventListener("resize", schedule);
    schedule();
    return () => {
      cancelAnimationFrame(frame);
      resize.disconnect();
      mutations.disconnect();
      document.removeEventListener("scroll", schedule, true);
      document.removeEventListener("visibilitychange", schedule);
      window.removeEventListener("resize", schedule);
      notifyIsland({ type: "bot-avatar-bounds", botId: bot.botId, visible: false });
    };
  }, [bot?.botId, size, covered]);
  return bot
    ? <View nativeID="island-bot-avatar" accessibilityLabel={`${bot.name} animated Bot`} style={{ width: size, height: size }} />
    : <Mascot size={size} />;
}
