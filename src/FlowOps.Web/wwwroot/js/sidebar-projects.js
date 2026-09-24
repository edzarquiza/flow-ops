// Phase 29D: the Projects nav group's <details>/<summary> disclosure already works fully without
// this script — the server renders it open whenever the caller is inside one of their own
// projects, closed everywhere else, and native <details> handles every click/keyboard toggle on
// its own. This only adds two small conveniences on top:
//   1. Keeps `aria-expanded` on the <summary> in sync as the group is toggled (the server sets
//      the correct value once, at render time; this keeps it correct after that).
//   2. Remembers a manual collapse while the caller is browsing a project, so navigating to
//      another page inside that same project does not re-force it open every time — the one
//      thing a plain server-rendered default can't do across full page loads. It only ever
//      applies this memory on a project's own page; outside a project the group still always
//      starts collapsed, exactly as the server renders it.
// It is an external file so the CSP (script-src 'self') is unchanged; without it, the group still
// opens/closes correctly, aria-expanded still matches the server's own initial render, and the
// only thing missing is remembering a manual collapse across navigations.
(function () {
    "use strict";

    var STORAGE_KEY = "fo-projects-nav-open";

    var group = document.querySelector(".fo-sidebar__nav-group");
    if (!group) {
        return;
    }

    var summary = group.querySelector("summary");

    if (group.dataset.insideProject === "true") {
        try {
            var stored = window.localStorage.getItem(STORAGE_KEY);
            if (stored !== null) {
                group.open = stored === "true";
            }
        } catch (e) {
            // Private browsing / blocked storage — the server-rendered default stands.
        }
    }

    if (summary) {
        summary.setAttribute("aria-expanded", group.open ? "true" : "false");
    }

    group.addEventListener("toggle", function () {
        if (summary) {
            summary.setAttribute("aria-expanded", group.open ? "true" : "false");
        }

        try {
            window.localStorage.setItem(STORAGE_KEY, group.open ? "true" : "false");
        } catch (e) {
            // Ignore — this is a convenience, not a requirement.
        }
    });
})();
