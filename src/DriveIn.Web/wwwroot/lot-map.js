// Keyboard use of interactive lot maps (LotMap.razor, svg[data-roving]). The map is one tab stop: exactly one spot has
// tabindex 0 (Blazor renders them all -1; this picks one and moves it), the arrow keys move between spots (up is toward
// the screen, left is spot 1's side), Home and End go to the ends of the row, and Enter or Space clicks the focused spot,
// which Blazor handles like a mouse click. Moving focus happens here, so it costs no round trip to the server.
(() => {
    const spotSelector = "svg.lot-map[data-roving] [data-spot]";

    const spotsOf = map => [...map.querySelectorAll("[data-spot]")];
    const rowOf = el => Number(el.dataset.row);
    const numberOf = el => Number(el.dataset.spot);
    const inRow = (map, row) => spotsOf(map).filter(s => rowOf(s) === row).sort((a, b) => numberOf(a) - numberOf(b));

    // Keep one tab stop per map: the spot that had it, else the first spot that can be picked, else the first spot.
    function ensureTabStop(map) {
        const spots = spotsOf(map);
        if (spots.length === 0)
            return;
        const stops = spots.filter(s => s.getAttribute("tabindex") === "0");
        stops.slice(1).forEach(s => s.setAttribute("tabindex", "-1"));
        if (stops.length === 0)
            (spots.find(s => s.getAttribute("aria-disabled") !== "true") ?? spots[0]).setAttribute("tabindex", "0");
    }

    function moveTo(from, to) {
        if (!to || to === from)
            return;
        from.setAttribute("tabindex", "-1");
        to.setAttribute("tabindex", "0");
        to.focus();
    }

    // The spot in the next row up or down that's nearest across (rows are centered, so they don't line up by number).
    function across(map, spot, step) {
        const rows = [...new Set(spotsOf(map).map(rowOf))].sort((a, b) => a - b);
        const row = rows[rows.indexOf(rowOf(spot)) + step];
        if (row === undefined)
            return null;
        const x = Number(spot.dataset.x);
        return inRow(map, row).reduce((best, s) =>
            Math.abs(Number(s.dataset.x) - x) < Math.abs(Number(best.dataset.x) - x) ? s : best);
    }

    document.addEventListener("keydown", e => {
        const spot = e.target instanceof Element ? e.target.closest(spotSelector) : null;
        if (!spot || e.altKey || e.ctrlKey || e.metaKey)
            return;
        const map = spot.closest("svg.lot-map");
        const row = inRow(map, rowOf(spot));
        const index = row.indexOf(spot);
        let next;
        switch (e.key) {
            case "ArrowLeft": next = row[index - 1]; break;
            case "ArrowRight": next = row[index + 1]; break;
            case "ArrowUp": next = across(map, spot, -1); break;
            case "ArrowDown": next = across(map, spot, 1); break;
            case "Home": next = row[0]; break;
            case "End": next = row[row.length - 1]; break;
            case "Enter":
            case " ":
                e.preventDefault(); // Space would scroll the page
                if (spot.getAttribute("aria-disabled") !== "true")
                    spot.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
                return;
            default:
                return;
        }
        e.preventDefault(); // the arrow keys and Home/End would scroll the page
        moveTo(spot, next);
    });

    // A spot that's clicked takes the tab stop, so Tab comes back to it.
    document.addEventListener("click", e => {
        const spot = e.target instanceof Element ? e.target.closest(spotSelector) : null;
        if (spot && spot.getAttribute("tabindex") !== "0")
            moveTo(spotsOf(spot.closest("svg.lot-map")).find(s => s.getAttribute("tabindex") === "0") ?? spot, spot);
    });

    // Says it in the map's live region when the focused spot changes state under the viewer, e.g. someone else holds it.
    // The viewer's own picks are left to the page (SeatMap announces their hold), so a spot turning "spot-mine" isn't news.
    function announce(spot) {
        const live = spot.closest("svg.lot-map")?.nextElementSibling?.nextElementSibling;
        if (live?.hasAttribute("data-lot-live") && !spot.classList.contains("spot-mine"))
            live.textContent = `Just changed: ${spot.getAttribute("aria-label")}`;
    }

    let pending = false;
    const observer = new MutationObserver(mutations => {
        for (const m of mutations) {
            if (m.type === "attributes" && m.target === document.activeElement && m.target.matches(spotSelector)
                && m.oldValue !== m.target.getAttribute("aria-label"))
                announce(m.target);
        }
        if (!pending) {
            pending = true;
            requestAnimationFrame(() => {
                pending = false;
                document.querySelectorAll("svg.lot-map[data-roving]").forEach(ensureTabStop);
            });
        }
    });
    observer.observe(document.body, {
        childList: true, subtree: true, attributes: true, attributeFilter: ["aria-label"], attributeOldValue: true,
    });
    document.querySelectorAll("svg.lot-map[data-roving]").forEach(ensureTabStop);
})();
