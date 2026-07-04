// RagKit.Dashboard — vanilla JS, no build step. Every fetch() below is a path
// relative to the current page (guaranteed to end in "/" — see the redirect
// in RagDashboardExtensions), so it works no matter where MapRagDashboard was
// mounted.

async function api(method, path, body) {
  const resp = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!resp.ok) throw new Error(`${method} ${path} -> ${resp.status}`);
  const text = await resp.text();
  return text ? JSON.parse(text) : null;
}

function el(tag, props, ...children) {
  const e = document.createElement(tag);
  Object.entries(props || {}).forEach(([k, v]) => {
    if (k === "onclick") e.addEventListener("click", v);
    else e.setAttribute(k, v);
  });
  children.forEach((c) => e.append(c));
  return e;
}

function formData(form) {
  const data = Object.fromEntries(new FormData(form).entries());
  Object.keys(data).forEach((k) => { if (data[k] === "") data[k] = null; });
  return data;
}

// --- stats -----------------------------------------------------------------

async function loadStats() {
  const s = await api("GET", "api/stats");
  document.getElementById("stat-chunks").textContent = s.chunkCount;
  document.getElementById("stat-domains").textContent = s.domainCount;
  document.getElementById("stat-documents").textContent = s.documentCount;
}

// --- domains -----------------------------------------------------------------

async function loadDomains() {
  const domains = await api("GET", "api/domains");
  const body = document.querySelector("#table-domains tbody");
  body.innerHTML = "";
  domains.forEach((d) => {
    body.append(el("tr", {},
      el("td", {}, d.name),
      el("td", {}, d.description || ""),
      el("td", {}, el("button", {
        class: "danger",
        onclick: async () => {
          if (!confirm(`Borrar el dominio "${d.name}"? Esto borra también sus documentos, perfiles y guardarails.`)) return;
          await api("DELETE", `api/domains/${encodeURIComponent(d.name)}`);
          await Promise.all([loadDomains(), loadDocuments(), loadProfiles(), loadGuardrails(), loadStats()]);
        },
      }, "Borrar"))));
  });
}

document.getElementById("form-domain").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  await api("POST", "api/domains", formData(f));
  f.reset();
  await Promise.all([loadDomains(), loadStats()]);
});

// --- labels ------------------------------------------------------------------

async function loadLabels() {
  const labels = await api("GET", "api/labels");
  const body = document.querySelector("#table-labels tbody");
  body.innerHTML = "";
  labels.forEach((l) => body.append(el("tr", {}, el("td", {}, l.name), el("td", {}, l.description || ""))));
}

document.getElementById("form-label").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  await api("POST", "api/labels", formData(f));
  f.reset();
  await loadLabels();
});

// --- documents + chunks --------------------------------------------------------

async function loadDocuments(domain) {
  const qs = domain ? `?domain=${encodeURIComponent(domain)}` : "";
  const docs = await api("GET", `api/documents${qs}`);
  const body = document.querySelector("#table-documents tbody");
  body.innerHTML = "";
  docs.forEach((d) => {
    body.append(el("tr", {},
      el("td", {}, d.source),
      el("td", {}, d.domain || ""),
      el("td", {}, String(d.chunkCount)),
      el("td", {}, new Date(d.ingestedAtUtc).toLocaleString()),
      el("td", {},
        el("button", { class: "secondary", onclick: () => viewChunks(d.source, d.domain) }, "Chunks"),
        " ",
        el("button", {
          class: "danger",
          onclick: async () => {
            if (!confirm(`Borrar "${d.source}"?`)) return;
            await api("DELETE", `api/documents/${encodeURIComponent(d.source)}${d.domain ? `?domain=${encodeURIComponent(d.domain)}` : ""}`);
            await Promise.all([loadDocuments(domain), loadStats()]);
          },
        }, "Borrar"))));
  });
}

async function viewChunks(source, domain, cursor) {
  const viewer = document.getElementById("chunk-viewer");
  if (!cursor) viewer.innerHTML = `<h3>Chunks de ${source}</h3>`;
  const qs = new URLSearchParams({ take: "10", ...(domain ? { domain } : {}), ...(cursor ? { cursor } : {}) });
  const page = await api("GET", `api/documents/${encodeURIComponent(source)}/chunks?${qs}`);
  page.items.forEach((c) => {
    viewer.append(el("div", { class: "chunk" }, `[${c.id.slice(0, 8)}…] ${c.text}`));
  });
  const more = document.getElementById("btn-more-chunks");
  if (more) more.remove();
  if (page.nextCursor) {
    viewer.append(el("button", { id: "btn-more-chunks", class: "secondary", onclick: () => viewChunks(source, domain, page.nextCursor) }, "Cargar más"));
  }
}

document.getElementById("form-doc-filter").addEventListener("submit", async (e) => {
  e.preventDefault();
  await loadDocuments(formData(e.target).domain);
});

// --- ingest (SSE progress) ------------------------------------------------------

document.getElementById("form-ingest").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  const log = document.getElementById("ingest-log");
  log.innerHTML = "";

  let res;
  try {
    res = await api("POST", "api/ingest", {
      path: f.path.value,
      domain: f.domain.value || null,
      recursive: f.recursive.checked, // unlike formData(), a checkbox needs its live .checked read directly
    });
  } catch (err) {
    log.append(el("p", { class: "warn" }, String(err.message || err)));
    return;
  }

  const source = new EventSource(`api/ingest/${res.runId}/stream`);
  source.addEventListener("ingest", (ev) => {
    const payload = JSON.parse(ev.data);
    if (payload.done) {
      log.append(el("p", { class: payload.status === "Failed" ? "warn" : "muted" },
        payload.status === "Failed" ? `Fallo: ${payload.error}` : "Ingesta completada."));
      source.close();
      loadDocuments();
      loadStats();
      return;
    }
    const r = payload.result;
    log.append(el("p", {}, `${r.source}: ${r.outcome}${r.reason ? ` (${r.reason})` : ""}`));
  });
  source.onerror = () => source.close();
});

// --- guardrails ----------------------------------------------------------------

async function loadGuardrails() {
  const rules = await api("GET", "api/guardrails");
  const body = document.querySelector("#table-guardrails tbody");
  body.innerHTML = "";
  rules.forEach((r) => {
    body.append(el("tr", {},
      el("td", {}, r.description),
      el("td", {}, r.stage),
      el("td", {}, r.domain || ""),
      el("td", {}, r.profile || ""),
      el("td", {}, el("button", {
        class: "danger",
        onclick: async () => {
          if (!confirm(`Borrar el guardarail "${r.description}"?`)) return;
          await fetch("api/guardrails", {
            method: "DELETE",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ description: r.description, stage: r.stage, domain: r.domain, profile: r.profile }),
          });
          await loadGuardrails();
        },
      }, "Borrar"))));
  });
}

document.getElementById("form-guardrail").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  await api("POST", "api/guardrails", formData(f));
  f.reset();
  await loadGuardrails();
});

// --- profiles ------------------------------------------------------------------

async function loadProfiles() {
  const profiles = await api("GET", "api/profiles");
  const body = document.querySelector("#table-profiles tbody");
  body.innerHTML = "";
  profiles.forEach((p) => {
    body.append(el("tr", {},
      el("td", {}, p.name),
      el("td", {}, p.domain),
      el("td", {}, p.prompt || ""),
      el("td", {}, el("button", {
        class: "danger",
        onclick: async () => {
          if (!confirm(`Borrar el perfil "${p.name}"?`)) return;
          await api("DELETE", `api/profiles/${encodeURIComponent(p.domain)}/${encodeURIComponent(p.name)}`);
          await loadProfiles();
        },
      }, "Borrar"))));
  });
}

document.getElementById("form-profile").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  await api("POST", "api/profiles", formData(f));
  f.reset();
  await loadProfiles();
});

// --- prompts -------------------------------------------------------------------

async function loadPrompts() {
  const p = await api("GET", "api/prompts");
  document.getElementById("prompt-oneshot").value = p.oneShotPrompt || "";
  document.getElementById("prompt-chat").value = p.chatPrompt || "";
  const body = document.querySelector("#table-domain-prompts tbody");
  body.innerHTML = "";
  Object.entries(p.domainPrompts || {}).forEach(([domain, prompt]) => {
    body.append(el("tr", {},
      el("td", {}, domain),
      el("td", {}, prompt),
      el("td", {}, el("button", {
        class: "danger",
        onclick: async () => { await api("DELETE", `api/prompts/domain/${encodeURIComponent(domain)}`); await loadPrompts(); },
      }, "Borrar"))));
  });
}

document.getElementById("btn-save-prompts").addEventListener("click", async () => {
  await api("PUT", "api/prompts", {
    oneShotPrompt: document.getElementById("prompt-oneshot").value || null,
    chatPrompt: document.getElementById("prompt-chat").value || null,
  });
});

document.getElementById("form-domain-prompt").addEventListener("submit", async (e) => {
  e.preventDefault();
  const f = e.target;
  const { domain, prompt } = formData(f);
  await api("PUT", `api/prompts/domain/${encodeURIComponent(domain)}`, { prompt });
  f.reset();
  await loadPrompts();
});

// --- playground (Ask/AskStream) -------------------------------------------------

document.getElementById("form-ask").addEventListener("submit", (e) => {
  e.preventDefault();
  const { question, domain, profile } = formData(e.target);

  const citationsEl = document.getElementById("ask-citations");
  const answerEl = document.getElementById("ask-answer");
  citationsEl.innerHTML = "";
  answerEl.textContent = "";

  const qs = new URLSearchParams({ question, ...(domain ? { domain } : {}), ...(profile ? { profile } : {}) });
  const source = new EventSource(`api/ask/stream?${qs}`);
  source.addEventListener("ask", (ev) => {
    const payload = JSON.parse(ev.data);
    if (payload.citations) {
      payload.citations.forEach((c) => citationsEl.append(el("div", { class: "muted" }, `📎 ${c.source} — ${truncate(c.snippet)}`)));
      return;
    }
    if (payload.done) { source.close(); return; }
    answerEl.textContent += payload.token; // tokens arrive already in reading order
  });
  source.onerror = () => source.close();
});

function truncate(text) {
  return text.length > 120 ? text.slice(0, 120) + "…" : text;
}

// --- initial load ----------------------------------------------------------------

Promise.all([loadStats(), loadDomains(), loadLabels(), loadDocuments(), loadGuardrails(), loadProfiles(), loadPrompts()])
  .catch((err) => console.error("Error cargando el dashboard:", err));
