// Phase 24A (UI/UX stabilization): the Dashboard filter bar's auto-submit-on-change was
// previously an inline onchange="this.form.submit()" handler — CSP's script-src 'self' has no
// 'unsafe-inline', which blocks inline event-handler attributes exactly like it blocks inline
// <script> blocks, so those handlers never actually ran. This is the same behavior, moved into an
// external file so it runs under the existing policy. The Apply button remains the real
// mechanism (this is progressive enhancement only): with JavaScript disabled, or before this file
// loads, the form still submits normally.
(function () {
    "use strict";

    var selects = document.querySelectorAll("[data-autosubmit] select");
    for (var i = 0; i < selects.length; i++) {
        selects[i].addEventListener("change", function () {
            this.form.submit();
        });
    }
})();
