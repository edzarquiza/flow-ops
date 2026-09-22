// Phase 29C: instant appearance preview on Profile & Settings. The SERVER-rendered saved preference
// (<html data-theme>) is the source of truth on every request; this only lets the user SEE a choice
// before saving it, by setting the same attribute the server sets. Nothing is stored here: leaving the
// page without saving discards the preview, and coming back to it from the browser's back/forward cache
// restores the saved theme. It is an external file so the CSP (script-src 'self') is unchanged; without
// it the radios still work — they just apply after "Save appearance".
(function () {
    "use strict";

    var form = document.querySelector("[data-appearance-form]");
    if (!form) {
        return;
    }

    var root = document.documentElement;
    var saved = form.getAttribute("data-saved-theme") || root.getAttribute("data-theme") || "dark";

    function preview(theme) {
        // "system" is resolved by the stylesheet's prefers-color-scheme rule, so it follows the OS live.
        root.setAttribute("data-theme", theme);
    }

    form.addEventListener("change", function (event) {
        var input = event.target;
        if (input && input.name === "Appearance") {
            preview(String(input.value).toLowerCase());
        }
    });

    // Back/forward cache: an unsaved preview must not survive leaving and returning.
    window.addEventListener("pageshow", function (event) {
        if (!event.persisted) {
            return;
        }

        preview(saved);
        var radios = form.querySelectorAll("input[name=Appearance]");
        for (var i = 0; i < radios.length; i++) {
            radios[i].checked = String(radios[i].value).toLowerCase() === saved;
        }
    });
})();
