(() => {
    if (window.financeThemeLoaded) return;
    window.financeThemeLoaded = true;
    const key = 'financeforge.theme';
    const valid = value => ['light', 'dark', 'system'].includes(value) ? value : 'light';
    const device = window.matchMedia('(prefers-color-scheme: dark)');
    let choice = 'light';
    try { choice = valid(localStorage.getItem(key)); } catch { /* Private browsing can deny storage. */ }
    const syncSelectors = () => document.querySelectorAll('[data-theme-selector]').forEach(select => { select.value = choice; });
    function apply() {
        const theme = choice === 'system' ? (device.matches ? 'dark' : 'light') : choice;
        document.documentElement.dataset.theme = theme;
        document.documentElement.style.colorScheme = theme;
        syncSelectors();
    }
    apply(); // Runs before styles and the first paint, including on the login page.
    document.addEventListener('change', event => {
        if (!event.target.matches('[data-theme-selector]')) return;
        choice = valid(event.target.value);
        try { localStorage.setItem(key, choice); } catch { /* Keep this session usable. */ }
        apply();
    });
    device.addEventListener('change', () => { if (choice === 'system') apply(); });
    window.addEventListener('storage', event => {
        if (event.key === key || event.key === null) { choice = valid(event.newValue); apply(); }
    });
    // Blazor can replace the layout on navigation/reconnection without reloading this script.
    new MutationObserver(records => {
        if (records.some(record => [...record.addedNodes].some(node => node.nodeType === 1 &&
            (node.matches('[data-theme-selector]') || node.querySelector('[data-theme-selector]'))))) syncSelectors();
    }).observe(document.documentElement, { childList: true, subtree: true });
})();
