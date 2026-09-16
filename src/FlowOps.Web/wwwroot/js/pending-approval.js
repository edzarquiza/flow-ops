// Phase 24A-Extension (ADR-0025): lightweight status polling for /Account/PendingApproval.
// Deliberately plain fetch + setInterval — no SignalR/WebSockets/SSE, no framework. Polls the
// page's own ?handler=Status endpoint, which identifies the caller solely via a same-origin
// cookie (never a client-supplied id), so this script never needs to know or send any identifier
// itself.
(function () {
    "use strict";

    var root = document.querySelector("[data-approval-status-url]");
    if (!root) {
        return;
    }

    var statusUrl = root.getAttribute("data-approval-status-url");
    var pollIntervalMs = 4000;
    var timerId = null;
    var stopped = false;

    function showState(state) {
        var panels = document.querySelectorAll("[data-approval-state]");
        for (var i = 0; i < panels.length; i++) {
            panels[i].hidden = panels[i].getAttribute("data-approval-state") !== state;
        }
    }

    function announce(text) {
        var live = document.getElementById("approval-live-region");
        if (live) {
            live.textContent = text;
        }
    }

    function stopPolling() {
        stopped = true;
        if (timerId !== null) {
            window.clearInterval(timerId);
            timerId = null;
        }
    }

    // Only "Active" and "Rejected" ever change what is shown — every other value (Pending,
    // Unknown, or anything unrecognized) leaves the caller in the waiting state. A failed or
    // malformed response is never treated as evidence of rejection (spec §20).
    function applyStatus(status) {
        if (status === "Active") {
            showState("approved");
            announce("Your account has been approved. You can now sign in.");
            stopPolling();
        } else if (status === "Rejected") {
            showState("rejected");
            announce("Your account was not approved.");
            stopPolling();
        }
    }

    function poll() {
        fetch(statusUrl, { credentials: "same-origin", headers: { Accept: "application/json" } })
            .then(function (response) {
                return response.ok ? response.json() : null;
            })
            .then(function (data) {
                if (data && typeof data.status === "string") {
                    applyStatus(data.status);
                }
            })
            .catch(function () {
                // Network failure — silently retry on the next tick.
            });
    }

    function startPolling() {
        if (stopped || timerId !== null) {
            return;
        }

        poll();
        timerId = window.setInterval(poll, pollIntervalMs);
    }

    // Pause while the tab is hidden, resume when it becomes visible again — never leaves a
    // background timer running indefinitely (spec §21/§22).
    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            if (timerId !== null) {
                window.clearInterval(timerId);
                timerId = null;
            }
        } else {
            startPolling();
        }
    });

    window.addEventListener("pagehide", stopPolling);

    startPolling();
})();
