import { useSyncExternalStore } from "react";
import { installFoldBridge, setIslandFolded } from "./island-fold";

export type IslandBotSession = {
  botId: string;
  name: string;
  color: string;
  token: string;
  apiUrl: string;
};
type Bridge = { postMessage: (message: unknown) => void;
  addEventListener: (name: string, listener: (event: { data: unknown }) => void) => void };
type HostWindow = Window & { __ISLAND_HOST__?: IslandBotSession; chrome?: { webview?: Bridge } };
const hostWindow = typeof window === "undefined" ? undefined : window as HostWindow;
let current = hostWindow?.__ISLAND_HOST__;
const listeners = new Set<() => void>();
export const islandSession = () => current;
export function useIslandBot() {
  return useSyncExternalStore((listener) => {
    listeners.add(listener);
    return () => { listeners.delete(listener); };
  }, islandSession, () => undefined);
}
export function notifyIsland(message: Record<string, unknown>) {
  if (current) hostWindow?.chrome?.webview?.postMessage(message);
}
if (current && hostWindow?.chrome?.webview) {
  installFoldBridge(() => notifyIsland({ type: "expand-chat" }));
  hostWindow.chrome.webview.addEventListener("message", (event) => {
    const message = event.data as { type?: string; session?: IslandBotSession; folded?: boolean };
    if (message.type === "fold-layout" && typeof message.folded === "boolean") {
      setIslandFolded(message.folded);
      return;
    }
    if (message.type !== "select-bot" || !message.session
      || typeof message.session.botId !== "string" || typeof message.session.token !== "string"
      || message.session.apiUrl !== current?.apiUrl) return;
    current = message.session;
    for (const listener of listeners) listener();
  });
  document.addEventListener("pointerdown", (event) => {
    const target = event.target instanceof Element ? event.target : undefined;
    if (event.button !== 0 || !target?.closest('#island-page-drag-region')
      || target.closest('button,a,input,textarea,select,[role="button"],[role="tab"],[contenteditable="true"]')) return;
    event.preventDefault();
    notifyIsland({ type: "drag-window" });
  });
}
