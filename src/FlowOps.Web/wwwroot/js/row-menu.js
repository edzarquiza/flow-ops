// Row/card action menus (ADR-0030). The menus are plain <details class="row-menu"> elements, which
// work with no JavaScript; this only adds the behaviour a native <details> lacks: at most ONE menu is
// open at a time, and a menu closes on an outside click, on Escape (returning focus to its "..."
// button), and when keyboard focus leaves it. Without this file the menus still open and close.
(function () {
    "use strict";

    var SELECTOR = "details.row-menu";

    function openMenus() {
        return document.querySelectorAll(SELECTOR + "[open]");
    }

    // 'toggle' does not bubble, so listen in the capture phase on the document.
    document.addEventListener("toggle", function (event) {
        var menu = event.target;
        if (!menu.matches || !menu.matches(SELECTOR) || !menu.open) {
            return;
        }

        openMenus().forEach(function (other) {
            if (other !== menu) {
                other.open = false;
            }
        });

        keepInViewport(menu);
    }, true);

    // The panel opens beside its button (CSS). On a narrow screen that can run off an edge — the
    // first board column at ~768px pushed it past the left edge — so after opening, measure it and, if
    // needed, slide it just far enough to sit inside the viewport. Uses the CSSOM (not a style
    // attribute in markup), which the page's style-src 'self' policy allows.
    function keepInViewport(menu) {
        var panel = menu.querySelector(".row-menu__panel");
        if (!panel) {
            return;
        }

        panel.style.removeProperty("left");
        panel.style.removeProperty("right");

        var margin = 8;
        var viewport = document.documentElement.clientWidth;
        var rect = panel.getBoundingClientRect();
        if (rect.left >= margin && rect.right <= viewport - margin) {
            return;
        }

        var anchor = menu.getBoundingClientRect();
        var left = Math.min(Math.max(rect.left, margin), Math.max(margin, viewport - rect.width - margin));
        panel.style.setProperty("right", "auto");
        panel.style.setProperty("left", (left - anchor.left) + "px");
    }

    document.addEventListener("click", function (event) {
        openMenus().forEach(function (menu) {
            if (!menu.contains(event.target)) {
                menu.open = false;
            }
        });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key !== "Escape") {
            return;
        }

        var menu = openMenus()[0];
        if (menu) {
            menu.open = false;
            var summary = menu.querySelector("summary");
            if (summary) {
                summary.focus();
            }
        }
    });

    document.addEventListener("focusin", function (event) {
        openMenus().forEach(function (menu) {
            if (!menu.contains(event.target)) {
                menu.open = false;
            }
        });
    });
})();
