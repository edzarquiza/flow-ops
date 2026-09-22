// Project board drag-and-drop (ADR-0030) — a mouse enhancement over the native HTML5 drag events.
// It is NOT required to operate the board: every drag has an explicit action in the card's "..."
// menu, keyboard and touch users never need it, and without this file the board is fully usable.
//
// It decides nothing. A drop only fills the page's real <form> (ticket id + target column, plus a
// reason/resolution from a native <dialog> when the move needs one) and submits it: the server
// re-plans the move with the same code the menu actions use, authorizes it, and re-renders the board.
// Nothing is moved optimistically in the browser, so it cannot diverge from the server. The
// "valid/invalid" highlighting comes from the server-rendered data-targets on each card.
(function () {
    "use strict";

    var board = document.querySelector("[data-board]");
    var form = document.getElementById("board-move-form");
    var dialog = document.getElementById("board-move-dialog");
    var status = document.getElementById("board-dnd-status");
    if (!board || !form || !dialog || typeof dialog.showModal !== "function") {
        return;
    }

    var columns = board.querySelectorAll("[data-column]");
    var dragged = null;
    var origin = null;

    function clearState() {
        board.classList.remove("is-dragging-active");
        columns.forEach(function (column) {
            column.classList.remove("is-valid-target", "is-invalid-target", "is-origin", "is-drop-over");
        });
        if (dragged) {
            dragged.classList.remove("is-dragging");
        }
    }

    board.addEventListener("dragstart", function (event) {
        var card = event.target.closest && event.target.closest("[data-ticket-id]");
        if (!card || card.getAttribute("draggable") !== "true") {
            return;
        }

        dragged = card;
        origin = card.closest("[data-column]").getAttribute("data-column");
        var allowed = (card.getAttribute("data-targets") || "").split(",");

        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", card.getAttribute("data-ticket-id"));
        card.classList.add("is-dragging");
        board.classList.add("is-dragging-active");
        columns.forEach(function (column) {
            var key = column.getAttribute("data-column");
            column.classList.add(key === origin ? "is-origin" : allowed.indexOf(key) >= 0 ? "is-valid-target" : "is-invalid-target");
        });
    });

    board.addEventListener("dragend", function () {
        clearState();
        dragged = null;
        origin = null;
    });

    columns.forEach(function (column) {
        // Every column accepts a drop: an invalid one is submitted too, so the SERVER explains why
        // it was refused in the normal notice — the highlighting is a hint, never the authority.
        column.addEventListener("dragover", function (event) {
            if (!dragged) {
                return;
            }
            event.preventDefault();
            event.dataTransfer.dropEffect = "move";
            column.classList.add("is-drop-over");
        });

        column.addEventListener("dragleave", function (event) {
            if (!column.contains(event.relatedTarget)) {
                column.classList.remove("is-drop-over");
            }
        });

        column.addEventListener("drop", function (event) {
            if (!dragged) {
                return;
            }
            event.preventDefault();
            var target = column.getAttribute("data-column");
            var card = dragged;
            var from = origin;
            clearState();
            dragged = null;
            origin = null;
            if (target !== from) {
                startMove(card, from, target);
            }
        });
    });

    function setField(name, on, required) {
        var wrapper = dialog.querySelector('[data-field="' + name + '"]');
        wrapper.hidden = !on;
        wrapper.querySelectorAll("input, select, textarea").forEach(function (control) {
            control.disabled = !on;
            control.required = on && required !== false;
        });
    }

    function startMove(card, from, target) {
        form.elements.ticketId.value = card.getAttribute("data-ticket-id");
        form.elements.target.value = target;

        var ref = card.querySelector(".q-ref");
        var name = ref ? ref.textContent.trim() : "this ticket";
        var needsReason = target === "Pending" || (from === "Done" && target === "Open");
        var needsResolution = target === "Done";

        if (!needsReason && !needsResolution) {
            if (status) {
                status.textContent = "Moving " + name + "…";
            }
            form.submit();
            return;
        }

        var title = dialog.querySelector("#board-move-title");
        var help = dialog.querySelector("#board-move-help");
        var reasonLabel = dialog.querySelector("#board-move-reason-label");
        setField("reason", needsReason);
        setField("resolve", needsResolution);
        dialog.querySelector("#board-move-notes").required = needsResolution;

        if (needsResolution) {
            title.textContent = "Resolve " + name;
            help.textContent = "Choose how it was resolved and add a short note (at least 10 characters).";
        } else if (target === "Pending") {
            title.textContent = "Put " + name + " on hold";
            help.textContent = "Time on hold does not count toward the SLA.";
            reasonLabel.textContent = "Reason for the hold";
        } else {
            title.textContent = "Reopen " + name;
            help.textContent = "A new SLA cycle starts when a ticket is reopened.";
            reasonLabel.textContent = "Reason for reopening";
        }

        dialog.showModal();
        var first = dialog.querySelector("textarea:not([disabled]), select:not([disabled])");
        if (first) {
            first.focus();
        }
    }

    dialog.querySelector("[data-dialog-cancel]").addEventListener("click", function () {
        dialog.close();
    });
})();
