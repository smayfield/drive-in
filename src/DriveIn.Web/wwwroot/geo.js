// The browser's location for the theater list's "Use my location": { lat, lon }, or { error } with "denied",
// "unsupported" or "unavailable" (which covers timeouts). Called from Theaters/Index.razor.
window.driveIn = window.driveIn || {};
window.driveIn.getPosition = () => new Promise(resolve => {
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
