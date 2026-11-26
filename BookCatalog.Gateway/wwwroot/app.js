const $ = (id) => document.getElementById(id);

const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

const badge = (text, kind) => `<span class="badge ${escapeHtml(kind)}">${escapeHtml(text)}</span>`;

function toast(message, isError = false) {
    const element = $("toast");
    element.textContent = message;
    element.className = `toast show${isError ? " error" : ""}`;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => element.className = "toast", 3000);
}

async function api(path, options = {}) {
    const response = await fetch(path, {
        headers: { "Content-Type": "application/json", ...(options.headers || {}) },
        ...options
    });
    const text = await response.text();
    const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
    if (!response.ok) throw new Error(data?.detail || data?.error || data?.title || `HTTP ${response.status}`);
    return data;
}

const circuitLabel = { closed: "CLOSED", open: "OPEN", halfOpen: "HALF-OPEN" };

function renderBackends(backends) {
    if (!backends.length) {
        $("backends").innerHTML = `<tr><td colspan="6" class="muted">Niciun API înregistrat.</td></tr>`;
        return;
    }
    $("backends").innerHTML = backends.map(b => {
        const id = escapeHtml(b.id);
        const status = b.registry !== "UP" ? badge(b.registry, b.registry.toLowerCase()) : badge(b.health, b.health.toLowerCase());
        const heartbeat = b.registry === "UP" ? `<span class="muted">heartbeat ${(b.millisecondsSinceHeartbeat / 1000).toFixed(0)}s</span>` : "";
        const countdown = b.circuit === "open" && b.circuitOpenUntil
            ? ` <span class="muted">${Math.max(0, Math.ceil((new Date(b.circuitOpenUntil) - Date.now()) / 1000))}s</span>` : "";
        const failures = b.circuit === "closed" && b.consecutiveFailures > 0
            ? ` <span class="muted">${b.consecutiveFailures} eșec</span>` : "";
        return `<tr>
            <td><b>${id}</b></td>
            <td${b.lastHealthError ? ` title="${escapeHtml(b.lastHealthError)}"` : ""}>${status} ${heartbeat}</td>
            <td>${badge(circuitLabel[b.circuit] || b.circuit, b.circuit.toLowerCase())}${countdown}${failures}</td>
            <td>${b.requests}${b.failures ? ` <span style="color:var(--red)">/ ${b.failures} erori</span>` : ""}</td>
            <td>${b.lastLatencyMs} ms</td>
            <td class="actions">
                <button class="btn kill" data-service="${id}" data-state="stopped">Kill</button>
                <button class="btn neutral" data-service="${id}" data-state="running">Restart</button>
                <button class="btn ${b.faultSimulated ? "start" : "neutral"}" data-fault="${id}" data-enabled="${!b.faultSimulated}">
                    ${b.faultSimulated ? "Oprește eroarea" : "Simulează eroare"}</button>
            </td>
        </tr>`;
    }).join("");
}

function renderQueue(queue) {
    for (const level of ["high", "normal", "low"]) {
        const data = queue[level];
        $(`q-${level}`).textContent = data.waiting;
        $(`q-${level}-info`).textContent = `${data.processed} procesate · ${data.averageWaitMs} ms`;
    }
    $("q-active").textContent = queue.active;
    $("q-max").textContent = queue.maxConcurrency;
}

function renderRecent(recent) {
    $("recent").innerHTML = recent.map(r => {
        const statusKind = r.statusCode >= 500 ? "down" : r.statusCode >= 400 ? "halfopen" : "up";
        const cacheKind = r.cache === "HIT" ? "hit" : r.cache === "MISS" ? "miss" : "";
        const path = r.path.length > 45 ? r.path.slice(0, 45) + "…" : r.path;
        return `<tr>
            <td class="muted">${new Date(r.time).toLocaleTimeString("ro-RO")}</td>
            <td title="${escapeHtml(r.path)}">${escapeHtml(r.method)} ${escapeHtml(path)}</td>
            <td>${badge(r.statusCode, statusKind)}</td>
            <td>${escapeHtml(r.backend)}${r.sticky ? " 📌" : ""}${r.attempts > 1 ? ` <span class="muted">(${r.attempts} încercări)</span>` : ""}</td>
            <td>${r.cache === "-" ? "" : badge(r.cache, cacheKind)}</td>
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
        $("discovery-state").innerHTML = `Service Discovery ${stats.discovery.reachable ? badge("UP", "up") : badge("DOWN", "down")}`;

        renderBackends(stats.backends);
        renderQueue(stats.queue);

        const { latency, totals, recent } = stats.metrics;
        $("p50").textContent = latency.p50;
        $("p95").textContent = latency.p95;
        $("p99").textContent = latency.p99;
        $("total").textContent = totals.requests;
        $("errors").textContent = totals.errors;
        $("retries").textContent = totals.retries;
        renderRecent(recent);

        const cache = stats.cache;
        $("hit-ratio").textContent = cache.hitRatio;
        $("hits").textContent = cache.hits;
        $("misses").textContent = cache.misses;
        $("keys").textContent = cache.keys;
        $("ttl").textContent = cache.ttlSeconds;
    } catch {
        $("live").classList.add("offline");
        $("clock").textContent = "offline";
    }
}

async function refreshContainers() {
    try {
        const containers = await api("/gateway/containers");
        $("containers").innerHTML = containers.map(c => {
            const service = escapeHtml(c.service);
            const action = c.protected ? ""
                : c.state === "running"
                    ? `<button class="btn kill" data-service="${service}" data-state="stopped">Kill</button>`
                    : `<button class="btn start" data-service="${service}" data-state="running">Start</button>`;
            return `<tr><td><b>${service}</b></td><td>${badge(c.state.toUpperCase(), c.state)}</td><td>${action}</td></tr>`;
        }).join("");
    } catch (error) {
        $("containers").innerHTML = `<tr><td class="muted">${escapeHtml(error.message)}</td></tr>`;
    }
}

async function refreshDatabases() {
    try {
        const items = await api("/gateway/databases");
        const up = items.filter(d => d.status === "up");
        const inSync = up.length > 1 &&
            up.every(d => d.info.books === up[0].info.books && d.info.latestSyncVersion === up[0].info.latestSyncVersion);
        $("sync-state").innerHTML = up.length === 0 ? badge("FĂRĂ DATE", "down")
            : inSync ? badge("SINCRONIZATE", "up") : badge("SE SINCRONIZEAZĂ", "halfopen");
        $("databases").innerHTML = items.map(d => {
            if (d.status !== "up") {
                return `<div class="db"><div class="db-head"><b>${escapeHtml(d.backend)}</b> ${badge("DOWN", "down")}</div></div>`;
            }
            const i = d.info;
            const where = i.replicaSet ? `${escapeHtml(i.replicaSet)} · primary ${escapeHtml(i.primary)}` : "standalone";
            return `<div class="db">
                <div class="db-head"><b>${escapeHtml(d.backend)} → ${escapeHtml(i.database)}</b></div>
                <div class="db-count">${i.books} <span class="muted" style="font-size:.85rem">cărți</span></div>
                <div class="db-meta">${where}</div>
            </div>`;
        }).join("");
    } catch (error) {
        $("databases").innerHTML = `<span class="muted">${escapeHtml(error.message)}</span>`;
    }
}

const refreshAll = () => { refreshStats(); refreshContainers(); refreshDatabases(); };

document.addEventListener("click", async (event) => {
    const stateButton = event.target.closest("[data-service]");
    if (stateButton) {
        const { service, state } = stateButton.dataset;
        stateButton.disabled = true;
        try {
            await api(`/gateway/containers/${encodeURIComponent(service)}/state`, { method: "PUT", body: JSON.stringify({ state }) });
            toast(state === "stopped" ? `${service} oprit` : `${service} pornește`);
        } catch (error) {
            toast(error.message, true);
        }
        refreshAll();
        return;
    }

    const faultButton = event.target.closest("[data-fault]");
    if (faultButton) {
        const enabled = faultButton.dataset.enabled === "true";
        await api(`/gateway/backends/${encodeURIComponent(faultButton.dataset.fault)}/fault`, { method: "PUT", body: JSON.stringify({ enabled }) })
            .catch(error => toast(error.message, true));
        refreshStats();
        return;
    }

    const modeButton = event.target.closest("#balancing button");
    if (modeButton) {
        await api("/gateway/balancing", { method: "PUT", body: JSON.stringify({ mode: modeButton.dataset.mode }) })
            .catch(error => toast(error.message, true));
        refreshStats();
    }
});

$("reset-stats").addEventListener("click", async () => {
    await api("/gateway/statistics", { method: "DELETE" }).catch(error => toast(error.message, true));
    refreshStats();
});

$("clear-cache").addEventListener("click", async () => {
    await api("/gateway/cache", { method: "DELETE" }).catch(error => toast(error.message, true));
    refreshStats();
});

const sampleBook = { title: "Foundation", author: "Isaac Asimov", publishedYear: 1951, description: "A science fiction novel.", tags: ["science fiction"] };

function sampleBody(format) {
    if (format !== "xml") return JSON.stringify(sampleBook, null, 2);
    return `<Book>
  <Title>${sampleBook.title}</Title>
  <Author>${sampleBook.author}</Author>
  <PublishedYear>${sampleBook.publishedYear}</PublishedYear>
  <Description>${sampleBook.description}</Description>
  <Tags><Tag>${sampleBook.tags[0]}</Tag></Tags>
</Book>`;
}

function formatXml(xml) {
    const doc = new DOMParser().parseFromString(xml, "application/xml");
    if (doc.querySelector("parsererror")) return xml;
    const walk = (node, indent) => {
        const children = [...node.children];
        const attrs = [...node.attributes].filter(a => !a.name.startsWith("xmlns")).map(a => ` ${a.name}="${a.value}"`).join("");
        if (!children.length) return `${indent}<${node.nodeName}${attrs}>${node.textContent}</${node.nodeName}>`;
        return `${indent}<${node.nodeName}${attrs}>\n${children.map(c => walk(c, indent + "  ")).join("\n")}\n${indent}</${node.nodeName}>`;
    };
    return walk(doc.documentElement, "");
}

function syncBodyTemplate() {
    const method = $("try-method").value;
    const body = $("try-body");
    const needsBody = method === "POST" || method === "PUT";
    body.style.display = needsBody ? "" : "none";
    if (needsBody && (!body.value.trim() || body.dataset.auto === "true")) {
        body.value = sampleBody($("try-format").value);
        body.dataset.auto = "true";
    }
    if (method === "POST") $("try-path").value = "/api/books";
    else if (lastCreatedId && /^\/api\/books\/?$/.test($("try-path").value)) $("try-path").value = `/api/books/${lastCreatedId}`;
}

let lastCreatedId = null;

function clearResponse() {
    $("try-meta").innerHTML = "";
    $("try-response").textContent = "";
}

$("try-method").addEventListener("change", () => { clearResponse(); syncBodyTemplate(); });
$("try-format").addEventListener("change", () => {
    clearResponse();
    syncBodyTemplate();
    if ($("try-method").value === "GET") $("try-form").requestSubmit();
});
$("try-body").addEventListener("input", () => $("try-body").dataset.auto = "false");

$("try-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const method = $("try-method").value;
    const mime = $("try-format").value === "xml" ? "application/xml" : "application/json";
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
        const header = (name) => response.headers.get(name);
        const chips = [
            ["Status", response.status],
            ["Timp", `${Math.round(performance.now() - started)} ms`],
            ["API", header("X-Gateway-Backend")],
            ["Bază", header("X-Database")],
            ["Cache", header("X-Cache")],
            ["Încercări", header("X-Gateway-Attempts")]
        ].filter(([, value]) => value);
        $("try-meta").innerHTML = chips.map(([name, value]) => `<span>${name} <b>${escapeHtml(value)}</b></span>`).join("");

        let pretty = text;
        const contentType = header("Content-Type") || "";
        if (contentType.includes("json")) {
            try { pretty = JSON.stringify(JSON.parse(text), null, 2); } catch { /* keep raw */ }
        } else if (contentType.includes("xml")) {
            pretty = formatXml(text);
        }
        $("try-response").textContent = pretty || "(fără corp)";

        if (method === "POST" && response.status === 201) {
            const match = text.match(/"id"\s*:\s*"([0-9a-f]{24})"|<Id>([0-9a-f]{24})<\/Id>/i);
            const id = match && (match[1] || match[2]);
            if (id) {
                lastCreatedId = id;
                $("try-path").value = `/api/books/${id}`;
                $("try-method").value = "GET";
                syncBodyTemplate();
            }
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
    $("load-result").innerHTML = `<p class="muted">Rulează…</p>`;
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
        const rows = result.byPriority.map(p => `<tr><td>${p.priority}</td><td>${p.count}</td><td>${p.averageMs} ms</td></tr>`).join("");
        const backends = result.byBackend.map(b => `${escapeHtml(b.backend)}: ${b.count}`).join(" · ");
        const statuses = result.byStatus.map(s => `${s.status}: ${s.count}`).join(" · ");
        $("load-result").innerHTML = `
            <table class="table compact">
                <thead><tr><th>Prioritate</th><th>Cereri</th><th>Medie</th></tr></thead>
                <tbody>${rows}</tbody>
            </table>
            <p class="muted small-text">${result.total} cereri în ${result.durationMs} ms<br>
            API: ${backends}<br>Status: ${statuses}</p>`;
    } catch (error) {
        $("load-result").textContent = error.message;
    } finally {
        button.disabled = false;
        refreshStats();
    }
});

syncBodyTemplate();
refreshAll();
setInterval(refreshStats, 1000);
setInterval(refreshDatabases, 2000);
setInterval(refreshContainers, 3000);
