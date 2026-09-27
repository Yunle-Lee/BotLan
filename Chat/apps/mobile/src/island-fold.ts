/** Embedded-only layout switch. Keep the same React tree, controls and draft state. */
let folded = false;
let notifyExpand: (() => void) | undefined;
export function installFoldBridge(expand: () => void) {
  notifyExpand = expand;
  document.addEventListener("click", event => {
    if (!folded || !(event.target instanceof Element)) return;
    const action = event.target.closest('[role="button"],[role="tab"],button');
    if (action?.closest("#island-left-dock,#island-right-dock")) {
      // The original handler still runs (navigation/menu/language/etc.); simply
      // unfold its viewport instead of building a second set of fake controls.
      notifyExpand?.();
    }
  }, true);
}
export function setIslandFolded(value: boolean) {
  if (!document.getElementById("island-fold-style")) {
    const style = document.createElement("style");
    style.id = "island-fold-style";
    style.textContent = `
      html[data-island-folded="true"] #island-main-content,
      html[data-island-folded="true"] #island-page-drag-region { visibility:hidden; pointer-events:none; }
      html[data-island-folded="true"] body > div:not(#root),
      html[data-island-folded="true"] #island-toast { visibility:hidden; pointer-events:none; }
      html[data-island-folded="true"] #island-left-dock { left:4px; }
      html[data-island-folded="true"] #island-right-dock { right:13px; }
    `;
    document.head.appendChild(style);
  }
  folded = value;
  document.documentElement.dataset.islandFolded = String(value);
  // Report avatar bounds after the native viewport changes size.
  requestAnimationFrame(() => window.dispatchEvent(new Event("resize")));
}
