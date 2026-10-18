export function showEditor(panel, targetId) {
    if (!panel?.isConnected) return;
    const target = targetId ? document.getElementById(targetId) : null;
    const destination = target && panel.contains(target) ? target : panel;
    destination.scrollIntoView({ block: "start", behavior: "instant" });
    destination.focus({ preventScroll: true });
}
