// The offline gate page (ManageGateOffline.razor, GateOfflineEndpoints). Keeps tonight's admit list in IndexedDB,
// checks cars in on the device with the same rules as the online gate, queues the check-ins and sends them to the server
// whenever it can be reached. Ticket codes are only ever compared as SHA-256 hashes; the list holds no full codes.

const root = document.getElementById("gate-offline");
if (root)
    start(root);

// The same alphabet and rules as ShortCodes (Data/Ticket.cs).
const SHORT_CODE_ALPHABET = "ABCDEFGHJKMNPQRTUVWXYZ2346789";
const SHORT_CODE_LENGTH = 4;
const MAX_BATCH = 200; // TicketSalesService.MaxOfflineSyncBatch
const POLL_MS = 30_000;
const STALE_MS = 2 * 60 * 60 * 1000;
const CONFLICTS = new Set(["already_used", "not_found", "not_valid"]);

function start(root) {
    const theaterId = root.dataset.theaterId;
    const base = root.dataset.base;
    const tokenHeader = root.dataset.tokenHeader;
    const ref = name => root.querySelector(`[data-ref="${name}"]`);
    const els = {
        connection: ref("connection"), updated: ref("updated"), queue: ref("queue"), sync: ref("sync"), notice: ref("notice"),
        form: ref("form"), code: ref("code"), results: ref("results"), showings: ref("showings"),
        conflictsSection: ref("conflicts-section"), conflicts: ref("conflicts"), clearConflicts: ref("clear-conflicts"),
    };
    const keys = { list: `list:${theaterId}`, queue: `queue:${theaterId}`, conflicts: `conflicts:${theaterId}`, token: `token:${theaterId}` };

    // What the device knows. list = { etag, fetchedAt (ms), data: OfflineGateList }.
    const state = { list: null, queue: [], conflicts: [], token: null, connection: "unknown", syncing: false, lastFound: [] };

    if (!window.crypto?.subtle || !window.indexedDB) {
        showNotice("This browser can't run the offline gate (it needs a secure connection and IndexedDB). Use the online gate instead.");
        return;
    }

    // --- Time, in the theater's time zone (from the list) ---

    const zone = () => state.list?.data.timeZone || "UTC";
    const format = (ms, options) => new Intl.DateTimeFormat("en-US", { timeZone: zone(), ...options }).format(new Date(ms));
    const timeOf = ms => format(ms, { hour: "numeric", minute: "2-digit" });
    const dayTimeOf = ms => format(ms, { weekday: "short", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
    const localDate = ms => new Intl.DateTimeFormat("en-CA", { timeZone: zone(), year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date(ms));

    // --- Storage ---

    const db = new Promise((resolve, reject) => {
        const open = indexedDB.open("drivein-gate", 1);
        open.onupgradeneeded = () => open.result.createObjectStore("kv");
        open.onsuccess = () => resolve(open.result);
        open.onerror = () => reject(open.error);
    });
    async function read(key) {
        const d = await db;
        return new Promise((resolve, reject) => {
            const req = d.transaction("kv").objectStore("kv").get(key);
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }
    async function write(key, value) {
        const d = await db;
        return new Promise((resolve, reject) => {
            const tx = d.transaction("kv", "readwrite");
            tx.objectStore("kv").put(value, key);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    }

    // --- Matching what was typed or scanned ---

    function normalizeShortCode(input) {
        const s = (input || "").replace(/[\s-]/g, "").toUpperCase();
        return s.length === SHORT_CODE_LENGTH && [...s].every(c => SHORT_CODE_ALPHABET.includes(c)) ? s : null;
    }

    // As TicketLinks.CodeFrom: a bare code, or a ticket link (with any query or fragment).
    function codeFrom(input) {
        let text = (input || "").trim().split(/[?#]/)[0].replace(/\/+$/, "");
        text = text.slice(text.lastIndexOf("/") + 1);
        try {
            text = decodeURIComponent(text);
        } catch {
            // A malformed escape stays as it is, as on the server.
        }
        return text.trim();
    }

    // crypto.randomUUID is newer than SubtleCrypto in some browsers; a v4 UUID from getRandomValues does the same job.
    function newId() {
        if (crypto.randomUUID)
            return crypto.randomUUID();
        const b = crypto.getRandomValues(new Uint8Array(16));
        b[6] = (b[6] & 0x0f) | 0x40;
        b[8] = (b[8] & 0x3f) | 0x80;
        const h = [...b].map(x => x.toString(16).padStart(2, "0")).join("");
        return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
    }

    async function sha256(text) {
        const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
        return [...bytes].map(b => b.toString(16).padStart(2, "0")).join("");
    }

    const showingOf = ticket => state.list.data.showings.find(s => s.id === ticket.showingId);
    const queued = ticket => state.queue.find(q => q.ticketId === ticket.id);
    const admittedAt = ticket => ticket.admittedAt ? Date.parse(ticket.admittedAt) : queued(ticket) ? Date.parse(queued(ticket).admittedAt) : null;

    // As TicketSalesService.AdmitProblem.
    function problemOf(ticket, now = Date.now()) {
        const showing = showingOf(ticket);
        const admitted = admittedAt(ticket);
        if (admitted !== null)
            return `Already used: admitted ${dayTimeOf(admitted)}.`;
        const starts = Date.parse(showing.startsAt);
        if (now >= Date.parse(showing.endsAt))
            return `Not valid: this ticket was for ${showing.dateLabel} and can't be used on a later date.`;
        if (now < Date.parse(showing.gatesOpenAt))
            return localDate(starts) !== localDate(now)
                ? `Not valid today: this ticket is for ${showing.dateLabel} at ${timeOf(starts)}.`
                : `Too early: this ticket is for the ${timeOf(starts)} showing, and gates open ${showing.gatesOpenLabel}.`;
        return null;
    }

    async function find(input) {
        els.results.replaceChildren();
        if (!state.list) {
            showResultMessage("There's no ticket list on this device yet. Connect once to download tonight's tickets.");
            return;
        }
        const shortCode = normalizeShortCode(input);
        let matches;
        if (shortCode)
            matches = state.list.data.tickets.filter(t => t.shortCode === shortCode);
        else {
            const code = codeFrom(input);
            const hash = code ? await sha256(code) : null;
            matches = hash ? state.list.data.tickets.filter(t => t.codeHash === hash) : [];
        }
        if (matches.length === 0) {
            showResultMessage("No ticket for today's showings matches that code. Check it and try again. A ticket for another " +
                "day or another theater isn't on this device" + (state.connection === "online" ? "." : ", so use the online gate when the signal is back if you're unsure."));
            return;
        }
        // Valid ones first, then by showing.
        matches.sort((a, b) => (problemOf(a) ? 1 : 0) - (problemOf(b) ? 1 : 0)
            || Date.parse(showingOf(a).startsAt) - Date.parse(showingOf(b).startsAt));
        state.lastFound = matches.map(t => t.id);
        renderResults();
    }

    function showResultMessage(text) {
        state.lastFound = [];
        const div = el("div", { class: "gate-result invalid", role: "alert" }, el("p", { class: "gate-verdict" }, "Not found"), el("p", {}, text));
        els.results.replaceChildren(div);
    }

    function renderResults(justAdmittedId) {
        const tickets = state.lastFound.map(id => state.list?.data.tickets.find(t => t.id === id)).filter(Boolean);
        els.results.replaceChildren(...tickets.map(ticket => {
            const showing = showingOf(ticket);
            const problem = problemOf(ticket);
            const admitted = ticket.id === justAdmittedId;
            const kind = ticket.kind === "comp" ? "Free admission" : ticket.kind === "gate" ? "Sold at the gate" : "Bought online";
            const box = el("div", { class: `gate-result ${admitted ? "admitted" : problem ? "invalid" : "valid"}` },
                el("p", { class: "gate-verdict" }, admitted ? "Checked in" : problem ? "Not valid" : "Valid"),
                problem && !admitted ? el("p", {}, problem) : null,
                el("div", {}, el("span", { class: "gate-spot" }, `Spot ${ticket.spot}`), ` · ${showing.screen}`,
                    ticket.large ? el("span", { class: "gate-chip" }, "Large vehicle") : null),
                el("div", {}, showing.title),
                el("div", { class: "small" }, `${showing.startsLabel} · gate code `, el("span", { class: "font-monospace" }, ticket.shortCode || "—")),
                el("div", { class: "small gate-muted" }, kind + (ticket.test ? " · test ticket" : "")));
            if (!problem && !admitted) {
                const button = el("button", { type: "button", class: "btn btn-primary btn-lg w-100" }, "Check in");
                button.addEventListener("click", () => admit(ticket));
                box.append(button);
            }
            return box;
        }));
    }

    // --- Checking in ---

    async function admit(ticket) {
        if (problemOf(ticket))
            return renderResults();
        state.queue.push({ admissionId: newId(), ticketId: ticket.id, admittedAt: new Date().toISOString(), spot: ticket.spot });
        await write(keys.queue, state.queue);
        renderResults(ticket.id);
        renderStatus();
        renderShowings();
        els.code.value = "";
        els.code.focus();
        sync();
    }

    // --- Talking to the server ---

    function setConnection(value) {
        state.connection = value;
        renderStatus();
    }

    // Signed out, the server answers with the sign-in page (a redirect) instead of JSON.
    const signedOut = response => response.redirected || !(response.headers.get("Content-Type") || "").includes("json");

    async function refresh() {
        let response;
        try {
            response = await fetch(`${base}/data`, {
                credentials: "same-origin", cache: "no-store",
                headers: state.list?.etag ? { "If-None-Match": state.list.etag } : {},
            });
        } catch {
            return setConnection("offline");
        }
        if (response.headers.get(tokenHeader)) {
            state.token = response.headers.get(tokenHeader);
            await write(keys.token, state.token);
        }
        if (response.status === 304) {
            state.list.fetchedAt = Date.now();
            await write(keys.list, state.list);
        } else if (response.status === 403) {
            return setConnection("denied");
        } else if (!response.ok || signedOut(response)) {
            return setConnection(response.ok || response.status === 401 ? "signed-out" : "offline");
        } else {
            state.list = { etag: response.headers.get("ETag"), fetchedAt: Date.now(), data: await response.json() };
            await write(keys.list, state.list);
        }
        setConnection("online");
        renderShowings();
        if (state.lastFound.length)
            renderResults();
    }

    async function sync() {
        if (state.syncing)
            return;
        state.syncing = true;
        try {
            let retriedToken = false;
            while (state.queue.length > 0) {
                if (!state.token) {
                    await refresh();
                    if (!state.token || state.connection !== "online")
                        return;
                }
                const batch = state.queue.slice(0, MAX_BATCH);
                let response;
                try {
                    response = await fetch(`${base}/sync`, {
                        method: "POST", credentials: "same-origin", cache: "no-store",
                        headers: { "Content-Type": "application/json", RequestVerificationToken: state.token },
                        body: JSON.stringify({ admissions: batch }),
                    });
                } catch {
                    return setConnection("offline");
                }
                if (response.status === 400 && !retriedToken) {
                    const body = await response.json().catch(() => ({}));
                    if (body.error === "token") {
                        // The token went stale (e.g. signed in again): get a new one with the list, then try once more.
                        retriedToken = true;
                        state.token = null;
                        continue;
                    }
                    showNotice(body.message || "The server couldn't take these check-ins.");
                    return;
                }
                if (response.status === 403)
                    return setConnection("denied");
                if (!response.ok || signedOut(response))
                    return setConnection(response.ok || response.status === 401 ? "signed-out" : "offline");

                const { results } = await response.json();
                const done = new Set(results.map(r => r.admissionId));
                for (const r of results) {
                    const item = batch.find(q => q.admissionId === r.admissionId);
                    const ticket = state.list?.data.tickets.find(t => t.id === r.ticketId);
                    const showing = ticket ? showingOf(ticket) : null;
                    const what = `Spot ${item?.spot ?? "?"}${showing ? ` · ${showing.screen} · ${showing.title}` : ""}, checked in ${dayTimeOf(item ? Date.parse(item.admittedAt) : Date.now())}`;
                    if (CONFLICTS.has(r.outcome))
                        state.conflicts.push({ id: r.admissionId, info: false, what, message: r.message });
                    else if (r.currentSpot)
                        state.conflicts.push({ id: r.admissionId, info: true, what, message: `Moved since: the car's spot is now ${r.currentSpot}.` });
                    // Shown as used from now on, until the next list arrives with it.
                    if (ticket && !ticket.admittedAt && r.outcome !== "not_found")
                        ticket.admittedAt = item?.admittedAt;
                }
                state.queue = state.queue.filter(q => !done.has(q.admissionId));
                await write(keys.queue, state.queue);
                await write(keys.conflicts, state.conflicts);
                if (state.list)
                    await write(keys.list, state.list);
                setConnection("online");
                renderConflicts();
                renderShowings();
            }
        } finally {
            state.syncing = false;
            renderStatus();
        }
        await refresh();
    }

    // --- Rendering ---

    function showNotice(text) {
        els.notice.textContent = text;
        els.notice.hidden = !text;
    }

    function renderStatus() {
        const labels = { unknown: "Connecting…", online: "Online", offline: "Offline", "signed-out": "Signed out", denied: "No access" };
        els.connection.dataset.state = state.connection;
        els.connection.textContent = labels[state.connection];
        els.updated.textContent = state.list ? `List updated ${timeOf(state.list.fetchedAt)}.` : "No list on this device yet.";
        const waiting = state.queue.length;
        els.queue.textContent = waiting === 0 ? "All check-ins synced." : `${waiting} check-in${waiting === 1 ? "" : "s"} waiting to sync.`;
        els.sync.disabled = state.syncing;

        let notice = "";
        if (state.connection === "signed-out")
            notice = "You're signed out. Sign in again (open the online gate) so check-ins can sync; the ones on this device are kept.";
        else if (state.connection === "denied")
            notice = "You no longer have the \"Admit guests\" action here, so check-ins can't sync. Ask a manager.";
        else if (!state.list && state.connection === "offline")
            notice = "You're offline and there's no ticket list on this device yet. Connect once to download tonight's tickets.";
        else if (state.list && state.connection === "offline")
            notice = `Offline: tickets sold after ${timeOf(state.list.fetchedAt)} aren't on this device.`;
        else if (state.list && Date.now() - state.list.fetchedAt > STALE_MS)
            notice = `The list on this device is from ${dayTimeOf(state.list.fetchedAt)}; newer sales aren't on it.`;
        showNotice(notice);
    }

    function renderShowings() {
        if (!state.list) {
            els.showings.replaceChildren(el("p", { class: "gate-muted" }, "No list on this device yet."));
            return;
        }
        const { showings, tickets } = state.list.data;
        if (showings.length === 0) {
            els.showings.replaceChildren(el("p", { class: "gate-muted" }, "No showings left today."));
            return;
        }
        const head = el("tr", {}, el("th", { scope: "col" }, "Showing"), el("th", { scope: "col", class: "num" }, "In"),
            el("th", { scope: "col", class: "num" }, "Sold"));
        const rows = showings.map(s => {
            const sold = tickets.filter(t => t.showingId === s.id);
            const inCount = sold.filter(t => admittedAt(t) !== null).length;
            return el("tr", {}, el("td", {}, el("div", { class: "fw-semibold" }, s.title), el("div", { class: "small gate-muted" }, `${s.screen} · ${s.startsLabel} · gates ${s.gatesOpenLabel}`)),
                el("td", { class: "num" }, String(inCount)), el("td", { class: "num" }, String(sold.length)));
        });
        els.showings.replaceChildren(el("table", { class: "gate-showings" }, el("thead", {}, head), el("tbody", {}, ...rows)));
    }

    function renderConflicts() {
        els.conflictsSection.hidden = state.conflicts.length === 0;
        els.conflicts.replaceChildren(...state.conflicts.map(c =>
            el("li", { class: c.info ? "info" : "" }, el("div", { class: "fw-semibold" }, c.what), el("div", {}, c.message))));
    }

    function el(tag, attrs, ...children) {
        const node = document.createElement(tag);
        for (const [name, value] of Object.entries(attrs || {}))
            node.setAttribute(name, value);
        node.append(...children.filter(c => c !== null && c !== undefined && c !== false));
        return node;
    }

    // --- Offline copy of the page ---

    async function registerWorker() {
        if (!("serviceWorker" in navigator))
            return;
        try {
            await navigator.serviceWorker.register(`/manage/${theaterId}/gate/offline-sw.js`, { scope: base });
            const registration = await navigator.serviceWorker.ready;
            // Everything this page loaded from here, so the worker can keep it.
            const urls = [location.origin + location.pathname, ...performance.getEntriesByType("resource")
                .map(e => e.name)
                .filter(u => u.startsWith(location.origin) && !u.includes("/gate/offline/data") && !u.includes("/gate/offline/sync"))];
            registration.active?.postMessage({ type: "cache", urls: [...new Set(urls)] });
        } catch {
            // Without the worker the page still works while it stays open.
        }
    }

    // --- Start ---

    els.form.addEventListener("submit", e => {
        e.preventDefault();
        find(els.code.value).catch(() => showResultMessage("Something went wrong looking that code up. Try again, or use the online gate."));
    });
    els.sync.addEventListener("click", () => sync());
    els.clearConflicts.addEventListener("click", async () => {
        state.conflicts = [];
        await write(keys.conflicts, state.conflicts);
        renderConflicts();
    });
    window.addEventListener("online", () => sync());
    window.addEventListener("offline", () => setConnection("offline"));

    (async () => {
        state.list = (await read(keys.list)) ?? null;
        state.queue = (await read(keys.queue)) ?? [];
        state.conflicts = (await read(keys.conflicts)) ?? [];
        state.token = (await read(keys.token)) ?? null;
        renderStatus();
        renderShowings();
        renderConflicts();
        await sync();
        registerWorker();
        setInterval(() => sync(), POLL_MS);
    })();
}
