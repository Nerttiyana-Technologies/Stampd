// Lightweight theme switcher. Persists in localStorage so the choice survives navigation.
// Used directly via onclick="stampdTheme.toggle()" on the topbar button so the toggle
// works even on Static SSR pages where Blazor's @onclick wouldn't.

(function () {
    const KEY = 'stampd-theme';

    function apply(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem(KEY, theme); } catch (_) { /* ignore */ }
        // Sync the toggle button's icon.
        document.querySelectorAll('[data-theme-toggle]').forEach(el => {
            el.textContent = theme === 'dark' ? '☀' : '☾';
            el.setAttribute('aria-label', theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
            el.setAttribute('title',      theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
        });
    }

    // Apply stored preference on initial load (runs before Blazor hydrates).
    const stored = (() => {
        try { return localStorage.getItem(KEY); } catch (_) { return null; }
    })();
    if (stored === 'dark' || stored === 'light') {
        apply(stored);
    }

    function toggle() {
        const current = document.documentElement.getAttribute('data-theme') || 'light';
        apply(current === 'dark' ? 'light' : 'dark');
    }

    // Re-sync the toggle button icon after each Blazor enhanced-navigation render so
    // the icon stays consistent when navigating between pages.
    document.addEventListener('DOMContentLoaded', () => {
        const t = document.documentElement.getAttribute('data-theme') || 'light';
        apply(t);
    });

    // Blazor enhanced-navigation PATCHES the <html> element's attributes against
    // whatever the server rendered. The server doesn't know the user's theme
    // (it lives in localStorage), so it sends <html lang="en"> with no data-theme,
    // and Blazor strips our attribute on every page transition.
    //
    // Restore from localStorage after every enhanced-load. The 'enhancedload' DOM
    // event is fired by blazor.web.js after each enhanced navigation completes.
    function restoreFromStorage() {
        try {
            const stored = localStorage.getItem(KEY);
            if (stored === 'dark' || stored === 'light') {
                apply(stored);
            }
        } catch (_) { /* ignore */ }
    }
    document.addEventListener('enhancedload', restoreFromStorage);
    // Also belt-and-braces against any path that wipes the attribute mid-session:
    // if data-theme on <html> is ever removed/changed away from our stored value,
    // put it back.
    try {
        const observer = new MutationObserver(function (mutations) {
            for (const m of mutations) {
                if (m.type !== 'attributes' || m.attributeName !== 'data-theme') continue;
                const stored = localStorage.getItem(KEY);
                if (stored !== 'dark' && stored !== 'light') return;
                const current = document.documentElement.getAttribute('data-theme');
                if (current !== stored) { apply(stored); }
            }
        });
        observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    } catch (_) { /* MutationObserver not supported — fall back to enhancedload only */ }

    window.stampdTheme = {
        set: apply,
        toggle: toggle,
        get: () => document.documentElement.getAttribute('data-theme') || 'light',
    };
})();
