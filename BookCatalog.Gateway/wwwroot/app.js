const $ = (id) => document.getElementById(id);

const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

const badge = (text, kind) => `<span class="badge ${escapeHtml(kind)}">${escapeHtml(text)}</span>`;

function toast(message, isError = false) {
    const element = $("toast");
    element.textContent = message;
    element.className = `toast show${isError ? " error" : ""}`;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => element.className = "toast", 3500);
}

async function api(path, options = {}) {
    const response = await fetch(path, {
        headers: { "Content-Type": "application/json", ...(options.headers || {}) },
        ...options
    });
    const text = await response.text();
    const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
    if (!response.ok) {
        throw new Error(data?.detail || data?.error || data?.title || `HTTP ${response.status}`);
    }
    return data;
}

const circuitLabel = { closed: "CLOSED", open: "OPEN", halfOpen: "HALF-OPEN" };

function renderBackends(backends) {
    const body = $("backends");
    if (!backends.length) {
        body.innerHTML = `<tr><td colspan="8" class="muted">Niciun API înregistrat încă în Service Discovery.</td></tr>`;
        return;
    }

    body.innerHTML = backends.map(b => {
        const heartbeat = b.registry === "UP"
            ? `${badge("UP", "up")} <span class="muted">acum ${(b.millisecondsSinceHeartbeat / 1000).toFixed(1)}s · #${b.heartbeatCount}</span>`
            : badge(b.registry, b.registry.toLowerCase());
        const circuit = b.circuit;
        const circuitInfo = circuit === "open" && b.circuitOpenUntil
            ? ` <span class="muted">${Math.max(0, Math.ceil((new Date(b.circuitOpenUntil) - Date.now()) / 1000))}s</span>`
            : "";
        const healthTitle = b.lastHealthError ? ` title="${escapeHtml(b.lastHealthError)}"` : "";
        return `<tr>
            <td><b>${escapeHtml(b.id)}</b></td>
            <td class="muted">${escapeHtml(b.address)}</td>
            <td${healthTitle}>${badge(b.health, b.health.toLowerCase())} <span class="muted">${b.lastHealthCheckMs} ms</span></td>
            <td>${heartbeat}</td>
            <td>${badge(circuitLabel[circuit] || circuit, circuit.toLowerCase())}${circuitInfo}
                <span class="muted">(${b.consecutiveFailures} eșecuri la rând)</span></td>
            <td>${b.requests} / <span style="color:var(--red)">${b.failures}</span></td>
            <td>${b.lastLatencyMs} ms</td>
            <td class="actions">
                <button class="btn kill" data-service="${escapeHtml(b.id)}" data-state="stopped">☠ Kill</button>
                <button class="btn start" data-service="${escapeHtml(b.id)}" data-state="running">⟳ Restart</button>
                <button class="btn neutral" data-circuit="${escapeHtml(b.id)}">Reset circuit</button>
            </td>
        </tr>`;
    }).join("");
}

function renderQueue(queue) {
    for (const level of ["high", "normal", "low"]) {
        const data = queue[level];
        $(`q-${level}`).textContent = data.waiting;
        $(`q-${level}-info`).textContent =
            `${data.processed} procesate · ${data.averageWaitMs} ms așteptare · vârf ${data.peakWaiting}`;
    }
    $("q-active").textContent = queue.active;
    $("q-max").textContent = queue.maxConcurrency;
}

function renderRecent(recent) {
    $("recent").innerHTML = recent.map(r => {
        const time = new Date(r.time).toLocaleTimeString("ro-RO");
        const statusKind = r.statusCode >= 500 ? "down" : r.statusCode >= 400 ? "halfopen" : "up";
        const cacheKind = r.cache === "HIT" ? "hit" : r.cache === "MISS" ? "miss" : "";
        return `<tr>
            <td class="muted">${time}</td>
            <td>${escapeHtml(r.method)}</td>
            <td title="${escapeHtml(r.path)}">${escapeHtml(r.path.length > 48 ? r.path.slice(0, 48) + "…" : r.path)}</td>
            <td>${badge(r.statusCode, statusKind)}</td>
            <td>${escapeHtml(r.backend)}${r.sticky ? " 📌" : ""}${r.attempts > 1 ? ` <span class="muted">(${r.attempts} încercări)</span>` : ""}</td>
            <td>${r.cache === "-" ? '<span class="muted">-</span>' : badge(r.cache, cacheKind)}</td>
            <td>${escapeHtml(r.priority)}</td>
            <td>${r.latencyMs}</td>
        </tr>`;
    }).join("");
}

async function refreshStats() {
    try {
        const stats = await api("/gateway/stats");
        $("live").classList.remove("offline");
        $("clock").textContent = new Date(stats.time).toLocaleTimeString("ro-RO");

        document.querySelectorAll("#balancing button").forEach(button =>
            button.classList.toggle("active", button.dataset.mode.toLowerCase() === stats.balancing.toLowerCase()));

        $("discovery-state").innerHTML = stats.discovery.reachable
            ? `discovery: ${badge("UP", "up")} <span class="muted">${escapeHtml(stats.discovery.url)}</span>`
            : `discovery: ${badge("DOWN", "down")} <span class="muted">${escapeHtml(stats.discovery.lastError || "")}</span>`;

        renderBackends(stats.backends);
        renderQueue(stats.queue);

        const { latency, totals, recent } = stats.metrics;
        $("p50").textContent = latency.p50;
        $("p95").textContent = latency.p95;
        $("p99").textContent = latency.p99;
        $("total").textContent = totals.requests;
        $("rps").textContent = totals.requestsPerSecond;
        $("errors").textContent = totals.errors;
        $("retries").textContent = totals.retries;
        renderRecent(recent);

        const cache = stats.cache;
        $("hit-ratio").textContent = cache.hitRatio;
        $("hits").textContent = cache.hits;
        $("misses").textContent = cache.misses;
        $("keys").textContent = cache.keys;
        $("ttl").textContent = cache.ttlSeconds;
        $("bypassed").textContent = cache.bypassed;
        $("invalidations").textContent = cache.invalidations;
    } catch (error) {
        $("live").classList.add("offline");
        $("clock").textContent = "offline";
    }
}

async function refreshContainers() {
    try {
        const containers = await api("/gateway/containers");
        $("containers").innerHTML = containers.map(c => {
            const actions = c.protected
                ? '<span class="muted">protejat</span>'
                : c.state === "running"
                    ? `<button class="btn kill" data-service="${escapeHtml(c.service)}" data-state="stopped">Kill</button>`
                    : `<button class="btn start" data-service="${escapeHtml(c.service)}" data-state="running">Start</button>`;
            return `<tr>
                <td><b>${escapeHtml(c.service)}</b><br><span class="muted">${escapeHtml(c.status)}</span></td>
                <td>${badge(c.state.toUpperCase(), c.state)}</td>
                <td class="actions">${actions}</td>
            </tr>`;
        }).join("");
    } catch (error) {
        $("containers").innerHTML = `<tr><td colspan="3" class="muted">${escapeHtml(error.message)}</td></tr>`;
    }
}

async function refreshShards() {
    try {
        const response = await fetch("/api/shards", { headers: { "Cache-Control": "no-cache", "X-Priority": "low", "X-Dashboard": "1" } });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const shards = await response.json();
        const total = shards.reduce((sum, s) => sum + Math.max(0, s.books), 0) || 1;
        $("shards").innerHTML = shards.map(s => {
            const up = s.status === "up";
            const share = up ? Math.round(100 * s.books / total) : 0;
            const members = (s.members || []).map(m => m === s.primary ? `<b>${escapeHtml(m)}</b> (primary)` : escapeHtml(m)).join(", ");
            return `<div class="shard">
                <div class="shard-head"><b>${escapeHtml(s.name)}</b> ${badge(up ? "UP" : "DOWN", up ? "up" : "down")}</div>
                <div class="shard-count">${up ? s.books : "—"} <span class="muted" style="font-size:.85rem">cărți · ${share}%</span></div>
                <div class="shard-bar"><span style="width:${share}%"></span></div>
                <div class="shard-meta">
                    Interval hash: <b>${escapeHtml(s.range.from)} – ${escapeHtml(s.range.to)}</b><br>
                    Baza: <b>${escapeHtml(s.database)}</b> · replica set <b>${escapeHtml(s.replicaSet || "?")}</b><br>
                    ${up ? `Noduri: ${members}` : `<span style="color:var(--red)">${escapeHtml(s.error || "")}</span>`}
                </div>
            </div>`;
        }).join("");
    } catch (error) {
        $("shards").innerHTML = `<span class="muted">${escapeHtml(error.message)}</span>`;
    }
}

document.addEventListener("click", async (event) => {
    const stateButton = event.target.closest("[data-service]");
    if (stateButton) {
        const { service, state } = stateButton.dataset;
        stateButton.disabled = true;
        try {
            await api(`/gateway/containers/${encodeURIComponent(service)}/state`, {
                method: "PUT",
                body: JSON.stringify({ state })
            });
            toast(state === "stopped" ? `${service} a fost oprit (kill).` : `${service} pornește…`);
        } catch (error) {
            toast(error.message, true);
        } finally {
            stateButton.disabled = false;
            refreshContainers();
            refreshStats();
        }
        return;
    }

    const circuitButton = event.target.closest("[data-circuit]");
    if (circuitButton) {
        await api(`/gateway/backends/${encodeURIComponent(circuitButton.dataset.circuit)}/circuit`, { method: "PUT" })
            .then(() => toast(`Circuitul ${circuitButton.dataset.circuit} a fost închis.`))
            .catch(error => toast(error.message, true));
        refreshStats();
        return;
    }

    const modeButton = event.target.closest("#balancing button");
    if (modeButton) {
        await api("/gateway/balancing", { method: "PUT", body: JSON.stringify({ mode: modeButton.dataset.mode }) })
            .then(() => toast(`Load balancing: ${modeButton.textContent}`))
            .catch(error => toast(error.message, true));
        refreshStats();
    }
});

$("refresh").addEventListener("click", () => { refreshStats(); refreshContainers(); refreshShards(); });

$("reset-stats").addEventListener("click", async () => {
    await api("/gateway/statistics", { method: "DELETE" }).catch(error => toast(error.message, true));
    toast("Statisticile au fost resetate.");
    refreshStats();
});

$("clear-cache").addEventListener("click", async () => {
    try {
        const result = await api("/gateway/cache", { method: "DELETE" });
        toast(`Cache golit: ${result.deleted} chei șterse.`);
    } catch (error) {
        toast(error.message, true);
    }
    refreshStats();
});

const sampleBook = {
    title: "Foundation",
    author: "Isaac Asimov",
    publishedYear: 1951,
    description: "A science fiction novel.",
    tags: ["science fiction"]
};

function sampleBody(format) {
    if (format === "xml") {
        return `<Book>
  <Title>${sampleBook.title}</Title>
  <Author>${sampleBook.author}</Author>
  <PublishedYear>${sampleBook.publishedYear}</PublishedYear>
  <Description>${sampleBook.description}</Description>
  <Tags><Tag>${sampleBook.tags[0]}</Tag></Tags>
</Book>`;
    }
    return JSON.stringify(sampleBook, null, 2);
}

function syncBodyTemplate() {
    const method = $("try-method").value;
    const body = $("try-body");
    const needsBody = method === "POST" || method === "PUT";
    body.disabled = !needsBody;
    if (needsBody && (!body.value.trim() || body.dataset.auto === "true")) {
        body.value = sampleBody($("try-format").value);
        body.dataset.auto = "true";
    }
    if (method === "POST") $("try-path").value = "/api/books";
}

$("try-method").addEventListener("change", syncBodyTemplate);
$("try-format").addEventListener("change", syncBodyTemplate);
$("try-body").addEventListener("input", () => $("try-body").dataset.auto = "false");

$("try-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const method = $("try-method").value;
    const format = $("try-format").value;
    const mime = format === "xml" ? "application/xml" : "application/json";
    const headers = { "Accept": mime, "X-Priority": $("try-priority").value };
    if ($("try-client").value.trim()) headers["X-Client-Id"] = $("try-client").value.trim();
    if ($("try-nocache").checked) headers["Cache-Control"] = "no-cache";

    const options = { method, headers };
    if (method === "POST" || method === "PUT") {
        headers["Content-Type"] = mime;
        options.body = $("try-body").value;
    }

    const started = performance.now();
    try {
        const response = await fetch($("try-path").value, options);
        const text = await response.text();
        const elapsed = Math.round(performance.now() - started);
        const shown = ["X-Gateway-Backend", "X-Api-Instance", "X-Shard", "X-Cache", "X-Cache-TTL", "X-Priority", "X-Balancing", "X-Gateway-Attempts", "Content-Type"];
        $("try-meta").innerHTML = [`<span>Status <b>${response.status}</b></span>`, `<span>Timp <b>${elapsed} ms</b></span>`]
            .concat(shown.filter(h => response.headers.get(h)).map(h => `<span>${h} <b>${escapeHtml(response.headers.get(h))}</b></span>`))
            .join("");

        let pretty = text;
        if ((response.headers.get("Content-Type") || "").includes("json")) {
            try { pretty = JSON.stringify(JSON.parse(text), null, 2); } catch { /* keep raw */ }
        }
        $("try-response").textContent = pretty || "(fără corp)";

        if (method === "POST" && response.status === 201) {
            const match = text.match(/"id"\s*:\s*"([0-9a-f]{24})"|<Id>([0-9a-f]{24})<\/Id>/i);
            const id = match && (match[1] || match[2]);
            if (id) $("try-path").value = `/api/books/${id}`;
        }
    } catch (error) {
        $("try-response").textContent = error.message;
    }
    refreshStats();
});

$("load-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = $("load-run");
    button.disabled = true;
    $("load-result").textContent = "Rulează…";
    try {
        const result = await api("/gateway/load-tests", {
            method: "POST",
            body: JSON.stringify({
                total: Number($("load-total").value),
                highPercent: Number($("load-high").value),
                lowPercent: Number($("load-low").value),
                bypassCache: $("load-bypass").checked
            })
        });
        const rows = result.byPriority.map(p =>
            `<tr><td>${p.priority}</td><td>${p.count}</td><td>${p.averageMs}</td><td>${p.maxMs}</td></tr>`).join("");
        const backends = result.byBackend.map(b => `${escapeHtml(b.backend)}: ${b.count}`).join(" · ");
        const statuses = result.byStatus.map(s => `HTTP ${s.status}: ${s.count}`).join(" · ");
        $("load-result").innerHTML = `
            <table class="table compact">
                <thead><tr><th>Prioritate</th><th>Cereri</th><th>Medie ms</th><th>Max ms</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>
            <p class="muted small-text">${result.total} cereri în ${result.durationMs} ms · max ${result.maxConcurrency} concurente<br>
            Distribuție: ${backends}<br>${statuses}</p>`;
    } catch (error) {
        $("load-result").textContent = error.message;
    } finally {
        button.disabled = false;
        refreshStats();
    }
});

syncBodyTemplate();
refreshStats();
refreshContainers();
refreshShards();
setInterval(refreshStats, 1000);
setInterval(refreshShards, 3000);
setInterval(refreshContainers, 3000);
