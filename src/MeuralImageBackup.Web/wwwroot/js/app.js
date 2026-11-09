// Small helpers called from Blazor through JS interop.
window.meural = {
    // Per-browser preferences (theme, what's new, the Images view). Storage can be unavailable
    // (private mode, blocked site data), so failures fall back to the default instead of breaking the page.
    getPref: (key) => {
        try { return localStorage.getItem("mib." + key); } catch { return null; }
    },
    setPref: (key, value) => {
        try { localStorage.setItem("mib." + key, value); } catch { }
    },

    // Switches the theme now (App.razor's script resolves "system") and remembers it.
    setTheme: (theme) => {
        window.meural.setPref("theme", theme);
        window.meuralApplyTheme(theme);
    },

    // Focuses the element unless the focus is in it already or a dialog is open over it.
    keepFocus: (element) => {
        if (!element || element.contains(document.activeElement) || document.querySelector(".modal")) return;
        element.focus({ preventScroll: true });
    },

    // Copies text to the clipboard; false when the browser doesn't allow it (plain http on
    // another host than localhost has no clipboard API), so the page can say to copy by hand.
    copy: async (text) => {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
};

// A confirmation dialog over the image viewer gives the focus back to the viewer when it closes.
new MutationObserver(() => {
    const viewer = document.querySelector(".viewer");
    if (viewer) window.meural.keepFocus(viewer);
}).observe(document.body, { childList: true, subtree: false });
