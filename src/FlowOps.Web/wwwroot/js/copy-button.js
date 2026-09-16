// Phase 24A (UI/UX stabilization): one shared copy-to-clipboard interaction, used wherever the
// app shows a value someone needs to paste elsewhere (the invitation link, an error reference id)
// — replacing Members' old onclick="this.select()" (also dead under CSP's script-src 'self',
// which has no 'unsafe-inline'). Event delegation, no globals: works for any button matching
// [data-copy-target] added to the page after this script loads, with no per-button wiring.
//
// Graceful without the Clipboard API (e.g. non-secure-context http:// local dev): the target
// value still gets selected, so a manual Ctrl+C/Cmd+C works, and the feedback text says so rather
// than silently claiming a copy that didn't happen.
(function () {
    "use strict";

    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-copy-target]");
        if (!button) {
            return;
        }

        var target = document.getElementById(button.getAttribute("data-copy-target"));
        if (!target) {
            return;
        }

        var text = "value" in target ? target.value : target.textContent;
        var feedback = document.getElementById(button.getAttribute("data-copy-feedback") || "");

        if (typeof target.select === "function") {
            target.select();
        }

        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(
                function () { announce(feedback, "Copied"); },
                function () { announce(feedback, "Press Ctrl+C to copy"); }
            );
        } else {
            announce(feedback, "Press Ctrl+C to copy");
        }
    });

    function announce(feedback, message) {
        if (feedback) {
            feedback.textContent = message;
        }
    }
})();
