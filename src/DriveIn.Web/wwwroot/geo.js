// "Use my location" on the (statically rendered) theater list, Theaters/Index.razor. The button ships hidden, so a browser
// without JavaScript just has the ZIP code / city search; this shows it, asks the browser for its location and opens
// theaters?lat=&lon=&radius= with the distance chosen in the form. Listeners are delegated from the document, since
// Blazor's enhanced navigation swaps the page's content without reloading this script.
(function () {
    function getPosition() {
        return new Promise(resolve => {
            if (!("geolocation" in navigator)) {
                resolve({ error: "unsupported" });
                return;
            }
            navigator.geolocation.getCurrentPosition(
                p => resolve({ lat: p.coords.latitude, lon: p.coords.longitude }),
                e => resolve({ error: e.code === e.PERMISSION_DENIED ? "denied" : "unavailable" }),
                // A coarse, recently cached position is plenty for finding nearby theaters.
                { enableHighAccuracy: false, timeout: 15000, maximumAge: 10 * 60 * 1000 });
        });
    }

    var messages = {
        denied: "Your browser didn't share your location. Allow it for this site, or type a ZIP code or city.",
        unsupported: "Your browser can't share your location. Type a ZIP code or city instead.",
        unavailable: "We couldn't get your location. Try again, or type a ZIP code or city."
    };

    function showButtons() {
        if ("geolocation" in navigator) {
            document.querySelectorAll("[data-geo-locate]").forEach(b => { b.hidden = false; });
        }
    }

    document.addEventListener("click", async e => {
        var button = e.target instanceof Element ? e.target.closest("[data-geo-locate]") : null;
        if (!button || button.disabled) {
            return;
        }
        var form = button.closest("form");
        var error = document.querySelector("[data-geo-error]");
        if (error) {
            error.hidden = true;
        }
        button.disabled = true;
        try {
            var position = await getPosition();
            if (position.error || typeof position.lat !== "number" || typeof position.lon !== "number") {
                if (error) {
                    error.textContent = messages[position.error] || messages.unavailable;
                    error.hidden = false;
                }
                return;
            }
            // Two decimals is about a kilometer: close enough to sort theaters, and it keeps the exact spot out of the URL.
            var query = new URLSearchParams({
                lat: position.lat.toFixed(2),
                lon: position.lon.toFixed(2),
                radius: (form && form.elements.radius && form.elements.radius.value) || "100"
            });
            var url = (form ? form.getAttribute("action") : "theaters") + "?" + query.toString();
            if (window.Blazor && typeof window.Blazor.navigateTo === "function") {
                window.Blazor.navigateTo(url);
            } else {
                window.location.assign(url);
            }
        } finally {
            button.disabled = false;
        }
    });

    showButtons();
    if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
        window.Blazor.addEventListener("enhancedload", showButtons);
    }
})();
