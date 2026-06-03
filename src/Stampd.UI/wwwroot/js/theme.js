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

    window.stampdTheme = {
        set: apply,
        toggle: toggle,
        get: () => document.documentElement.getAttribute('data-theme') || 'light',
    };
})();
