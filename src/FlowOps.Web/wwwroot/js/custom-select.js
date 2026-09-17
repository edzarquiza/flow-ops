// ADR-0028: the one deliberate exception to CLAUDE.md §11.1's "no unnecessary JavaScript" — no
// CSS technique can restyle a native <select> popup to match the app's dark theme, so this
// enhances every ordinary, non-multiple <select> into a themed button+listbox on load, while
// keeping the real <select> in the DOM as the actual form control (still gets its value posted
// under its own `name`, still the target of its <label for>, still what a screen reader or a
// no-JS visitor uses directly). If this script fails to load or run, every select falls back to
// being a plain, fully functional native control — nothing here is required for correctness.
//
// Follows the ARIA APG "Select-Only Combobox" pattern: focus stays on the trigger button the
// whole time (never moves into the listbox); the open listbox is announced via
// aria-activedescendant on the trigger, not by moving focus.
(function () {
    "use strict";

    var uid = 0;

    document.addEventListener("DOMContentLoaded", function () {
        var selects = document.querySelectorAll("select:not([data-fo-enhanced])");
        for (var i = 0; i < selects.length; i++) {
            enhance(selects[i]);
        }
    });

    function enhance(select) {
        // Multi-select has no equivalent single-value trigger label and is not used anywhere in
        // this app today — left as a plain native control rather than guessing a design for it.
        if (select.multiple) {
            return;
        }

        select.setAttribute("data-fo-enhanced", "true");

        // The select's own original classes (e.g. `.role-update-form__select`, sizing hooks used
        // in compact inline forms) move to the wrapper, since that's the element now actually
        // laid out in the page — a class-based CSS rule written against the select still applies,
        // unchanged, to whichever element is now visible.
        var wrapper = document.createElement("div");
        wrapper.className = select.className ? "fo-select " + select.className : "fo-select";
        select.parentNode.insertBefore(wrapper, select);
        wrapper.appendChild(select);

        // The trigger inherits the select's own id (so an existing <label for="..."> and any
        // asp-validation-for-driven aria-describedby keep pointing at the thing a user actually
        // interacts with); the now-inert select gets a derived internal id instead of none, so it
        // remains addressable for debugging without colliding with anything else on the page.
        var originalId = select.id;
        uid += 1;
        select.id = (originalId || "fo-select-" + uid) + "__native";

        var describedBy = select.getAttribute("aria-describedby");
        select.removeAttribute("aria-describedby");
        select.tabIndex = -1;
        select.setAttribute("aria-hidden", "true");
        select.className = select.className ? select.className + " fo-select__native" : "fo-select__native";

        var listboxId = select.id + "-listbox";

        var trigger = document.createElement("button");
        trigger.type = "button";
        trigger.className = "fo-select__trigger";
        if (originalId) {
            trigger.id = originalId;
        }
        trigger.setAttribute("aria-haspopup", "listbox");
        trigger.setAttribute("aria-expanded", "false");
        trigger.setAttribute("aria-controls", listboxId);
        if (describedBy) {
            trigger.setAttribute("aria-describedby", describedBy);
        }
        if (select.disabled) {
            trigger.disabled = true;
        }

        var valueSpan = document.createElement("span");
        valueSpan.className = "fo-select__value";
        trigger.appendChild(valueSpan);

        var caret = document.createElement("span");
        caret.className = "fo-select__caret";
        caret.setAttribute("aria-hidden", "true");
        trigger.appendChild(caret);

        wrapper.appendChild(trigger);

        var listbox = document.createElement("ul");
        listbox.className = "fo-select__listbox";
        listbox.id = listboxId;
        listbox.setAttribute("role", "listbox");
        listbox.hidden = true;
        wrapper.appendChild(listbox);

        var items = []; // { li, option } in document order, options only (group labels excluded)

        function buildItems(container, sourceNode) {
            for (var i = 0; i < sourceNode.children.length; i++) {
                var node = sourceNode.children[i];
                if (node.tagName === "OPTGROUP") {
                    var groupLabel = document.createElement("li");
                    groupLabel.className = "fo-select__group-label";
                    groupLabel.setAttribute("role", "presentation");
                    groupLabel.textContent = node.label;
                    container.appendChild(groupLabel);
                    buildItems(container, node);
                } else if (node.tagName === "OPTION") {
                    var li = document.createElement("li");
                    li.className = "fo-select__option";
                    li.id = listboxId + "-opt-" + items.length;
                    li.setAttribute("role", "option");
                    li.setAttribute("aria-selected", "false");
                    li.textContent = node.textContent;
                    if (node.disabled) {
                        li.setAttribute("aria-disabled", "true");
                        li.classList.add("is-disabled");
                    }
                    container.appendChild(li);
                    items.push({ li: li, option: node });
                }
            }
        }

        buildItems(listbox, select);

        var activeIndex = -1;
        var open = false;

        function selectedIndex() {
            for (var i = 0; i < items.length; i++) {
                if (items[i].option === select.options[select.selectedIndex]) {
                    return i;
                }
            }
            return -1;
        }

        function syncValueLabel() {
            var current = select.options[select.selectedIndex];
            valueSpan.textContent = current ? current.textContent : "";
            for (var i = 0; i < items.length; i++) {
                var isSelected = items[i].option === current;
                items[i].li.setAttribute("aria-selected", isSelected ? "true" : "false");
                items[i].li.classList.toggle("is-selected", isSelected);
            }
        }

        function setActive(index) {
            if (activeIndex >= 0 && items[activeIndex]) {
                items[activeIndex].li.classList.remove("is-active");
            }
            activeIndex = index;
            if (activeIndex >= 0 && items[activeIndex]) {
                var li = items[activeIndex].li;
                li.classList.add("is-active");
                trigger.setAttribute("aria-activedescendant", li.id);
                if (typeof li.scrollIntoView === "function") {
                    li.scrollIntoView({ block: "nearest" });
                }
            } else {
                trigger.removeAttribute("aria-activedescendant");
            }
        }

        function openListbox() {
            if (open || trigger.disabled) {
                return;
            }
            open = true;
            listbox.hidden = false;
            trigger.setAttribute("aria-expanded", "true");
            positionListbox();
            setActive(selectedIndex());
            document.addEventListener("click", onDocumentClick, true);
            window.addEventListener("scroll", closeOnScroll, true);
        }

        function closeListbox() {
            if (!open) {
                return;
            }
            open = false;
            listbox.hidden = true;
            trigger.setAttribute("aria-expanded", "false");
            trigger.removeAttribute("aria-activedescendant");
            document.removeEventListener("click", onDocumentClick, true);
            window.removeEventListener("scroll", closeOnScroll, true);
        }

        function closeOnScroll(event) {
            // A scroll of the listbox's own (scrollable, overflow-y) body must not close it —
            // only a scroll of some ancestor/the page, which would otherwise leave the popup
            // visually detached from its trigger.
            if (event.target === listbox || listbox.contains(event.target)) {
                return;
            }
            closeListbox();
        }

        function positionListbox() {
            listbox.classList.remove("fo-select__listbox--above");
            var triggerRect = trigger.getBoundingClientRect();
            var spaceBelow = window.innerHeight - triggerRect.bottom;
            var listboxHeight = listbox.getBoundingClientRect().height;
            if (spaceBelow < listboxHeight && triggerRect.top > listboxHeight) {
                listbox.classList.add("fo-select__listbox--above");
            }
        }

        function commit(index) {
            var item = items[index];
            if (!item || item.option.disabled) {
                return;
            }
            var changed = select.value !== item.option.value;
            select.value = item.option.value;
            syncValueLabel();
            closeListbox();
            trigger.focus();
            if (changed) {
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }
        }

        function onDocumentClick(event) {
            if (!wrapper.contains(event.target)) {
                closeListbox();
            }
        }

        trigger.addEventListener("click", function () {
            if (open) {
                closeListbox();
            } else {
                openListbox();
            }
        });

        trigger.addEventListener("keydown", function (event) {
            switch (event.key) {
                case "ArrowDown":
                    event.preventDefault();
                    if (!open) {
                        openListbox();
                    } else {
                        moveActive(1);
                    }
                    break;
                case "ArrowUp":
                    event.preventDefault();
                    if (!open) {
                        openListbox();
                    } else {
                        moveActive(-1);
                    }
                    break;
                case "Home":
                    if (open) {
                        event.preventDefault();
                        setActive(nextSelectable(0, 1));
                    }
                    break;
                case "End":
                    if (open) {
                        event.preventDefault();
                        setActive(nextSelectable(items.length - 1, -1));
                    }
                    break;
                case "Enter":
                case " ":
                    event.preventDefault();
                    if (open) {
                        commit(activeIndex);
                    } else {
                        openListbox();
                    }
                    break;
                case "Escape":
                    if (open) {
                        event.preventDefault();
                        closeListbox();
                    }
                    break;
                case "Tab":
                    closeListbox();
                    break;
                default:
                    typeahead(event);
                    break;
            }
        });

        listbox.addEventListener("click", function (event) {
            var li = event.target.closest(".fo-select__option");
            if (!li) {
                return;
            }
            var index = items.findIndex(function (item) { return item.li === li; });
            if (index >= 0) {
                commit(index);
            }
        });

        // Mouseover highlights the hovered option, matching how a native list responds to the
        // mouse — arrow-key navigation and hovering never fight over which option looks active.
        listbox.addEventListener("mousemove", function (event) {
            var li = event.target.closest(".fo-select__option");
            if (!li) {
                return;
            }
            var index = items.findIndex(function (item) { return item.li === li; });
            if (index >= 0 && index !== activeIndex) {
                setActive(index);
            }
        });

        function moveActive(direction) {
            var start = activeIndex < 0 ? (direction > 0 ? -1 : items.length) : activeIndex;
            setActive(nextSelectable(start + direction, direction));
        }

        function nextSelectable(start, direction) {
            var index = start;
            while (index >= 0 && index < items.length) {
                if (!items[index].option.disabled) {
                    return index;
                }
                index += direction;
            }
            return activeIndex;
        }

        var typeaheadBuffer = "";
        var typeaheadTimer = null;

        function typeahead(event) {
            if (event.key.length !== 1 || event.altKey || event.ctrlKey || event.metaKey) {
                return;
            }
            event.preventDefault();
            if (!open) {
                openListbox();
            }
            typeaheadBuffer += event.key.toLowerCase();
            window.clearTimeout(typeaheadTimer);
            typeaheadTimer = window.setTimeout(function () { typeaheadBuffer = ""; }, 600);

            for (var i = 0; i < items.length; i++) {
                var text = items[i].option.textContent.trim().toLowerCase();
                if (text.indexOf(typeaheadBuffer) === 0 && !items[i].option.disabled) {
                    setActive(i);
                    return;
                }
            }
        }

        // A native <select> can still change value from outside this component (e.g. a page
        // resetting a form) — keeping the visible label in sync with whatever the underlying
        // select actually holds, not just what this widget itself last set.
        select.addEventListener("change", syncValueLabel);

        syncValueLabel();
    }
})();
