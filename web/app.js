"use strict";
// A script error shows on the page rather than only in a console nobody has open.
function oops(text) {
  const box = document.getElementById("errors");
  if (box) box.insertAdjacentHTML("beforeend", `<p class="warnline">Dashboard error: ${String(text).replace(/</g, "&lt;")}</p>`);
}
window.addEventListener("error", e => oops(`${e.message} (${String(e.filename || "").split("/").pop()}:${e.lineno})`));
window.addEventListener("unhandledrejection", e => oops(e.reason && e.reason.stack || e.reason));
// Pilot dashboard. The server pushes the whole state over /api/stream whenever it changes, and an open
// session streams its own transcript. Views patch only what changed (put() compares HTML per slot), so
// nothing on the page is rebuilt that has not changed.

const $ = (sel, el = document) => el.querySelector(sel);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({"&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"}[c]));
const M = n => !n ? "0" : n >= 1e6 ? (n / 1e6).toFixed(n >= 1e7 ? 0 : 1) + "M" : n >= 1e3 ? Math.round(n / 1e3) + "k" : String(n);
const ago = t => { if (!t) return "never"; const s = Date.now() / 1000 - t; return s < 60 ? "just now" : s < 3600 ? Math.round(s / 60) + "m ago" : s < 86400 ? (s / 3600).toFixed(1) + "h ago" : Math.round(s / 86400) + "d ago"; };
const clock = iso => iso ? new Date(iso).toLocaleTimeString([], {hour: "2-digit", minute: "2-digit", second: "2-digit"}) : "";
const when = t => t ? new Date(t * 1000).toLocaleString([], {weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit"}) : "";
function put(el, html) { if (el && el._html !== html) { el.innerHTML = html; el._html = html; } }
function local(k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch { return null; } }
async function post(url, body) {
  const r = await fetch(url, {method: "POST", headers: {"X-Pilot": "1", "Content-Type": "application/json"}, body: JSON.stringify(body || {})});
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || `HTTP ${r.status}`);
  return j;
}

// ------------------------------------------------------------------ a small Markdown: enough for reports

function md(src) {
  const out = [], lines = String(src ?? "").replace(/\r/g, "").split("\n");
  let para = [], list = null;
  const inline = s => esc(s).replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>")
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
  const flushPara = () => { if (para.length) out.push(`<p>${para.map(inline).join("<br>")}</p>`); para = []; };
  const flushList = () => { if (list) out.push(`<${list.t}>${list.items.map(x => `<li>${inline(x)}</li>`).join("")}</${list.t}>`); list = null; };
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    let m;
    if (/^\s*```/.test(l)) {
      flushPara(); flushList();
      const body = [];
      while (++i < lines.length && !/^\s*```/.test(lines[i])) body.push(lines[i]);
      out.push(`<pre><code>${esc(body.join("\n"))}</code></pre>`);
    } else if ((m = /^#{1,6}\s+(.*)/.exec(l))) { flushPara(); flushList(); out.push(`<h4>${inline(m[1])}</h4>`); }
    else if ((m = /^\s*[-*]\s+(.*)/.exec(l)) || (m = /^\s*\d+[.)]\s+(.*)/.exec(l))) {
      const t = /^\s*\d/.test(l) ? "ol" : "ul";
      flushPara();
      if (!list || list.t !== t) { flushList(); list = {t, items: []}; }
      list.items.push(m[1]);
    } else if (!l.trim()) { flushPara(); flushList(); }
    else if (list && /^\s{2,}/.test(l)) list.items[list.items.length - 1] += " " + l.trim();
    else { flushList(); para.push(l); }
  }
  flushPara(); flushList();
  return `<div class="md">${out.join("")}</div>`;
}

// ------------------------------------------------------------------ shared pieces

function stateOf(w) {
  if (w.state === "working") return {cls: "working", label: "● working", stroke: "var(--fill)", tint: "var(--tint-fill)"};
  if (w.state === "blocked") return {cls: "blocked", label: "waiting on you", stroke: "var(--warn)", tint: "var(--tint-warn)"};
  if (w.state === "done" || w.final) {
    const head = w.reportHead || w.final?.reportHead;
    if (head === "FAILED") return {cls: "failed", label: "✗ failed", stroke: "var(--crit)", tint: "var(--tint-crit)"};
    if (head === "BLOCKED") return {cls: "blocked", label: "blocked: needs you", stroke: "var(--warn)", tint: "var(--tint-warn)"};
    if (head === "DONE") return {cls: "done", label: "✓ done", stroke: "var(--good)", tint: "var(--tint-good)"};
    return {cls: "other", label: "turn ended", stroke: "var(--axis)", tint: "var(--surface)"};
  }
  if (w.lastError) return {cls: "failed", label: "✗ error", stroke: "var(--crit)", tint: "var(--tint-crit)"};
  return {cls: "other", label: w.state || "unknown", stroke: "var(--axis)", tint: "var(--surface)"};
}
const chip = w => { const s = stateOf(w); return `<span class="chip ${s.cls}" title="${esc(w.detail || "")}">${esc(s.label)}</span>`; };

function meter(sp, budget) {
  sp = sp || {weighted: 0, tokens: {}};
  const t = sp.tokens || {};
  const tip = `input ${(t.input_tokens || 0).toLocaleString()} · cache write ${(t.cache_creation_input_tokens || 0).toLocaleString()} · cache read ${(t.cache_read_input_tokens || 0).toLocaleString()} · output ${(t.output_tokens || 0).toLocaleString()}\nweighted = input + 1.25×write + 0.1×read + 5×output`;
  if (!budget) return `<div class="meter" title="${esc(tip)}"><div class="lbl"><span>${M(sp.weighted)} weighted</span></div></div>`;
  const f = sp.weighted / budget, cls = f >= 1 ? "crit" : f >= 0.75 ? "warn" : "";
  const note = f >= 1 ? "⚠ over budget" : f >= 0.75 ? "▲ near budget" : "";
  return `<div class="meter" title="${esc(tip)}"><div class="lbl"><span>${M(sp.weighted)} / ${M(budget)}</span><span>${note}</span></div>
    <div class="track"><div class="bar ${cls}" style="width:${Math.min(100, f * 100).toFixed(1)}%"></div></div></div>`;
}

function doing(w) {
  if (w.state === "blocked" && w.detail) return `<span class="doing"><b>needs you:</b> ${esc(w.detail)}</span>`;
  if (w.state === "working" && w.now) return w.now.kind === "tool"
    ? `<span class="doing"><b>${esc(w.now.tool)}</b> ${esc(w.now.summary || w.now.text || "")}</span>` : `<span class="doing">${esc(w.now.text)}</span>`;
  if (w.detail) return `<span class="doing">${esc(w.detail)}</span>`;
  return w.now ? `<span class="doing">${esc(w.now.summary || w.now.text || "")}</span>` : `<span class="doing">nothing yet</span>`;
}

function actions(w, isWorker) {
  const a = [];
  if (w.id) a.push(`<button data-copy="claude attach ${esc(w.id)}" title="Copy the command that opens this session in a terminal">attach</button>`);
  if (w.remote) a.push(`<a class="btn" href="${esc(w.remote)}" target="_blank" rel="noopener" title="Talk to it from claude.ai or your phone (Remote Control)">remote ↗</a>`);
  if (isWorker && w.pid && w.state !== "gone") a.push(`<button class="danger" data-stop="${esc(w.name)}">stop</button>`);
  return `<div class="actions">${a.join("")}</div>`;
}

function limitsHtml(l) {
  if (!l || !l.windows) return `<span class="sub" title="Add the rate-limit cache to your status line (see README) to show the 5-hour and 7-day windows here.">no rate-limit figures</span>`;
  const out = [];
  for (const [key, label] of [["five_hour", "5-hour"], ["seven_day", "7-day"]]) {
    const w = l.windows[key]; if (!w) continue;
    const p = w.used_percentage ?? 0, cls = p >= 90 ? "crit" : p >= 75 ? "warn" : "";
    const resets = w.resets_at ? new Date(w.resets_at * 1000).toLocaleString([], {weekday: "short", hour: "2-digit", minute: "2-digit"}) : "";
    out.push(`<div class="limit meter" title="read ${esc(ago(l.writtenAt))}"><div class="lbl"><span>${label} ${Math.round(p)}%${p >= 90 ? " ⚠" : ""}</span><span>resets ${esc(resets)}</span></div>
      <div class="track"><div class="bar ${cls}" style="width:${Math.min(100, p)}%"></div></div></div>`);
  }
  return out.join("");
}

const sumErr = {};
function summaryParts(key, s) {
  const x = s.summaries?.[key], running = (s.running || []).includes(key);
  const body = running ? `<span class="sub">Summarising with Haiku…</span>`
    : (sumErr[key] ? `<div class="warnline">${esc(sumErr[key])}</div>` : "")
      + (x ? md(x.text) : `<span class="sub">No summary yet. One press is one Haiku call with no tools, about 10k tokens.</span>`);
  const stamp = x ? `${when(x.at)} · ${M(x.tokens)} tokens${x.ms ? ` · ${(x.ms / 1000).toFixed(0)} s` : ""}` : "";
  return {body, stamp, running};
}

// ------------------------------------------------------------------ connection, top bar, notifications

let S = null;
function connect() {
  if (new URLSearchParams(location.search).has("nostream")) {    // one look and no stream: for headless screenshots
    fetch("/api/state").then(r => r.json()).then(s => { S = s; conn(true); render(); });
    return;
  }
  const es = new EventSource("/api/stream");
  es.addEventListener("state", e => { S = JSON.parse(e.data); conn(true); desktop(); render(); });
  es.onopen = () => conn(true);
  es.onerror = () => conn(false);
}
function conn(ok) { const c = $("#conn"); c.className = "conn" + (ok ? " live" : ""); c.textContent = ok ? "live" : "reconnecting…"; }

const NOTE_ICON = {waiting: "🟡", done: "✅", failed: "❌", died: "❌", budget: "⚠️", quiet: "💤", turn: "•"};
function renderTop() {
  const r = parse();
  for (const a of document.querySelectorAll("nav a")) a.classList.toggle("on", a.dataset.nav === (r.view === "session" ? "" : r.view));
  if (!S) return;
  put($("#limits"), limitsHtml(S.limits));
  const over = S.workers.filter(w => w.overBudget), waiting = S.workers.filter(w => w.state === "blocked");
  put($("#alerts"),
    (over.length ? `<p class="warnline">⚠ Over budget: ${over.map(w => `<a href="#/w/${esc(w.name)}">${esc(w.name)}</a>`).join(", ")}. Their tools are refused until the budget is raised (<code>pilot budget NAME N</code>).</p>` : "") +
    (waiting.length ? `<p class="warnline amber">Waiting on you: ${waiting.map(w => `<a href="#/w/${esc(w.name)}">${esc(w.name)}</a>`).join(", ")}.</p>` : ""));
  const n = S.notifications || {unread: 0, items: []};
  const badge = $("#badge");
  badge.hidden = !n.unread; badge.textContent = n.unread > 99 ? "99+" : n.unread;
  document.title = (n.unread ? `(${n.unread}) ` : "") + "Pilot";
  put($("#notes"), n.items.length ? n.items.map(i => `<div class="note ${i.read ? "" : "unread"}" data-note="${esc(i.id)}" data-href="#/w/${esc(i.worker)}">
      <span>${NOTE_ICON[i.kind] || "•"}</span><div>${esc(i.text)}<div class="when">${esc(when(i.at))}</div></div></div>`).join("")
    : `<div class="empty">Nothing yet. Notices pile up here while the dashboard runs: a worker waiting on you, a turn that ended, a budget reached, 30 quiet minutes, an error.</div>`);
  $("#desk").textContent = "Notification" in window && Notification.permission === "granted" ? "desktop on" : "desktop";
}

function desktop() {
  const items = S.notifications?.items || [];
  const newest = items[0]?.id || "", seen = local("pilot.noteSeen");
  if (seen === null) { local("pilot.noteSeen", newest); return; }
  if ("Notification" in window && Notification.permission === "granted")
    for (const i of items.filter(i => !i.read && i.id > seen).reverse()) {
      const note = new Notification(`Pilot: ${i.worker}`, {body: i.text, tag: i.id});
      note.onclick = () => { window.focus(); location.hash = `#/w/${i.worker}`; };
    }
  if (newest > seen) local("pilot.noteSeen", newest);
}

// ------------------------------------------------------------------ rows, built once and patched

function place(list, items, empty, rows) {
  const keep = new Set(items.map(x => x.key));
  for (const [k, v] of rows) if (!keep.has(k)) { v.row.remove(); rows.delete(k); }
  let at = list.firstChild;
  for (const x of items) {
    let v = rows.get(x.key);
    if (!v) {
      const row = document.createElement("div");
      row.className = "row";
      row.innerHTML = `<div class="s-name name"></div><div class="s-chip"></div><div class="s-meter"></div><div class="s-doing"></div><div class="s-actions"></div>`;
      v = {row, name: $(".s-name", row), chip: $(".s-chip", row), meter: $(".s-meter", row), doing: $(".s-doing", row), actions: $(".s-actions", row)};
      rows.set(x.key, v);
    }
    v.row.dataset.href = x.href;
    if (at !== v.row) list.insertBefore(v.row, at);
    at = v.row.nextSibling;
    x.fill(v);
  }
  empty.hidden = items.length > 0;
}

// ------------------------------------------------------------------ flight plans

// A leg's state: its worker's if one flies it now, else how its removed worker landed, else planned.
function legState(s, name) {
  const w = s.workers.find(x => x.name === name);
  if (w) return {w, st: stateOf(w)};
  const h = s.history.find(x => x.name === name);
  if (h) return {h, st: {...stateOf(h), label: stateOf(h).label + " · removed"}};
  return {st: {cls: "other", label: "planned", stroke: "var(--axis)", tint: "var(--surface)", planned: true}};
}

function planCard(p, s) {
  const legs = p.legs.map(l => {
    const {w, h, st} = legState(s, l.leg);
    const sp = w ? w.spend : h ? h.final?.spend : null;
    const link = w ? `#/w/${esc(w.name)}` : h ? `#/s/${esc(h.sessionId)}` : "";
    return `<tr${link ? ` class="click" data-href="${link}"` : ""}><td><b>${esc(l.leg)}</b></td><td>${md(l.goal)}</td><td>${esc(l.after.join(", ") || "-")}</td>
      <td>${md(l.doneWhen)}</td><td><span class="chip ${st.cls}">${esc(st.label)}</span></td><td class="num">${sp ? esc(M(sp.weighted)) : ""}</td></tr>`;
  }).join("");
  const log = p.log.slice(-8).reverse().map(l => `<li>${esc(l)}</li>`).join("");
  return `<div class="card pad plan"><div class="cardhead"><b>Flight plan · ${esc(p.title)}</b>
      <span class="sub">${p.live ? `<span class="conn live">pilot ${esc(p.pilot || "")}</span> · ` : "pilot not running · "}updated ${esc(ago(p.updated))}
      · <a href="vscode://file/${esc(p.path.replace(/\\/g, "/"))}" title="${esc(p.path)}">edit</a> · <a href="#/s/${esc(p.sessionId)}">pilot session ›</a></span></div>
    ${p.objective ? `<div class="objective">${md(p.objective)}</div>` : ""}
    <div class="tablewrap"><table><thead><tr><th>Leg</th><th>Goal</th><th>After</th><th>Done when</th><th>Status</th><th class="num">Spend</th></tr></thead>
      <tbody>${legs || `<tr><td colspan="6" class="sub">No legs yet. The pilot adds them to the Route table.</td></tr>`}</tbody></table></div>
    ${log ? `<details><summary>flight log</summary><ul class="log">${log}</ul></details>` : ""}</div>`;
}

// ------------------------------------------------------------------ graph

// Every plan's legs hang under its pilot, chained by After; workers on no plan hang under the pilot that
// named itself when it started them. A planned leg no worker flies yet is drawn dashed.
function graphSvg(s) {
  const items = [], roots = new Map(), onPlan = new Set();
  for (const p of s.plans || []) {
    roots.set("p:" + p.sessionId, p.title);
    for (const l of p.legs) {
      onPlan.add(l.leg);
      const {w, st} = legState(s, l.leg);
      items.push({name: l.leg, after: l.after, root: "p:" + p.sessionId, w, st, started: w?.started || Infinity});
    }
  }
  for (const w of s.workers) if (!onPlan.has(w.name)) {
    const root = w.plan && roots.has("p:" + w.plan) ? "p:" + w.plan : "q:" + (w.pilot || "pilot");
    if (!roots.has(root)) roots.set(root, w.pilot || "pilot");
    items.push({name: w.name, after: w.after || [], root, w, st: stateOf(w), started: w.started || 0});
  }
  if (!items.length) return `<div class="empty">The graph fills in from the flight plan's route, and from workers as the pilot starts them. A leg with an After hangs under the leg it waits for.</div>`;
  const byName = new Map(items.map(x => [x.name, x])), depthOf = new Map();
  const depth = (x, path = new Set()) => {
    if (depthOf.has(x.name)) return depthOf.get(x.name);
    if (path.has(x.name)) return 1;
    path.add(x.name);
    const deps = x.after.filter(d => byName.has(d));
    const d = deps.length ? 1 + Math.max(...deps.map(n => depth(byName.get(n), path))) : 1;
    depthOf.set(x.name, d);
    return d;
  };
  items.forEach(x => depth(x));
  const layers = [[...roots].map(([key, label]) => ({key, pilot: label}))];
  for (const x of [...items].sort((a, b) => a.started - b.started)) (layers[depthOf.get(x.name)] ||= []).push({key: "w:" + x.name, x});
  const NW = 196, NH = 62, GX = 26, GY = 48, PAD = 8;
  const widest = Math.max(...layers.map(l => (l || []).length));
  const width = widest * (NW + GX) - GX + 2 * PAD, height = layers.length * (NH + GY) - GY + 2 * PAD;
  const pos = new Map();
  layers.forEach((l, i) => (l || []).forEach((n, j) => {
    const rowW = l.length * (NW + GX) - GX;
    pos.set(n.key, {x: PAD + (width - 2 * PAD - rowW) / 2 + j * (NW + GX), y: PAD + i * (NH + GY)});
  }));
  const edges = [];
  for (const x of items) {
    const deps = x.after.filter(d => byName.has(d));
    for (const from of deps.length ? deps.map(d => "w:" + d) : [x.root]) {
      const a = pos.get(from), b = pos.get("w:" + x.name);
      if (!a || !b) continue;
      const x1 = a.x + NW / 2, y1 = a.y + NH, x2 = b.x + NW / 2, y2 = b.y;
      edges.push(`<path class="edge" d="M${x1},${y1} C${x1},${y1 + GY / 2} ${x2},${y2 - GY / 2} ${x2},${y2}"/>`);
    }
  }
  const clip = (s, n) => s.length > n ? s.slice(0, n - 1) + "…" : s;
  const nodes = [];
  for (const l of layers) for (const n of l || []) {
    const {x, y} = pos.get(n.key);
    if (n.pilot) {
      nodes.push(`<g class="node"><rect class="box" x="${x}" y="${y}" width="${NW}" height="${NH}" rx="8" style="fill:var(--surface);stroke:var(--ink-2);stroke-width:1.5"/>
        <text x="${x + 12}" y="${y + 24}" font-weight="700">${esc(clip(n.pilot, 24))}</text><text class="sm" x="${x + 12}" y="${y + 44}">pilot</text></g>`);
      continue;
    }
    const {w, st, name} = n.x, f = w && w.budget ? Math.min(1, w.spend.weighted / w.budget) : 0;
    const dash = st.planned ? ";stroke-dasharray:5 4" : "";
    nodes.push(`<g class="node"${w ? ` data-href="#/w/${esc(w.name)}"` : ""}><title>${esc(w?.detail || (st.planned ? "planned, not started" : ""))}</title>
      <rect class="box" x="${x}" y="${y}" width="${NW}" height="${NH}" rx="8" style="fill:${st.tint};stroke:${st.stroke};stroke-width:1.5${dash}"/>
      <text x="${x + 12}" y="${y + 20}" font-weight="700">${esc(clip(name, 24))}</text>
      <text class="sm" x="${x + 12}" y="${y + 37}">${esc(st.label)}${w ? ` · ${esc(M(w.spend.weighted))}` : ""}</text>
      ${w ? `<rect x="${x + 12}" y="${y + 47}" width="${NW - 24}" height="4" rx="2" style="fill:var(--track)"/>
      <rect x="${x + 12}" y="${y + 47}" width="${Math.max(2, (NW - 24) * f)}" height="4" rx="2" style="fill:${f >= 1 ? "var(--crit)" : f >= .75 ? "var(--warn)" : "var(--fill)"}"/>` : ""}</g>`);
  }
  return `<svg width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="pilot and workers">${edges.join("")}${nodes.join("")}</svg>`;
}

// ------------------------------------------------------------------ views

const VIEWS = {};

VIEWS.overview = root => {
  root.innerHTML = `
    <div class="s-plans"></div>
    <h2>Radar</h2><div class="card graph s-graph"></div>
    <h2>Summary</h2>
    <div class="card pad"><div class="cardhead"><b>All workers</b><span><span class="sub s-stamp"></span> <button data-sum="*" class="s-btn">Summarise</button></span></div>
      <div class="summary s-sum" style="margin-top:6px"></div></div>
    <h2>Workers</h2><div class="card"><div class="list s-workers"></div>
      <div class="empty s-wempty">No workers. The pilot starts them with <code>pilot start NAME --brief FILE</code>.</div></div>
    <h2>Sessions in the main checkout</h2><div class="card"><div class="list s-pilots"></div>
      <div class="empty s-pempty">No session open in a repo's main checkout. Start the pilot there with <code>pilot</code>.</div></div>`;
  const rowsW = new Map(), rowsP = new Map();
  return {
    update(s) {
      const sp = summaryParts("*", s);
      put($(".s-sum", root), sp.body); put($(".s-stamp", root), esc(sp.stamp)); $(".s-btn", root).disabled = sp.running;
      put($(".s-plans", root), (s.plans || []).length ? s.plans.map(p => planCard(p, s)).join("")
        : `<div class="card pad"><b>No flight plan</b><div class="sub" style="margin-top:4px">The pilot opens one with <code>pilot plan</code> before it starts workers: the objective, the route of legs, and a log you can both read.</div></div>`);
      put($(".s-graph", root), graphSvg(s));
      place($(".s-workers", root), s.workers.map(w => ({key: w.name, href: `#/w/${w.name}`, fill: v => {
        put(v.name, `${esc(w.name)}<small>${esc(w.branch)} · ${esc(w.id)} · ${esc(ago(w.lastActivity))}</small>`);
        put(v.chip, chip(w)); put(v.meter, meter(w.spend, w.budget)); put(v.doing, doing(w)); put(v.actions, actions(w, true));
      }})), $(".s-wempty", root), rowsW);
      place($(".s-pilots", root), s.pilots.map(p => ({key: p.sessionId, href: `#/s/${p.sessionId}`, fill: v => {
        put(v.name, `${esc(p.name)}<small>${esc(p.kind)} · ${esc(ago(p.lastActivity))}</small>`);
        put(v.chip, `<span class="chip ${p.busy ? "working" : "other"}">${p.busy ? "● busy" : "idle"}</span>`);
        put(v.meter, meter(p.spend, 0)); put(v.doing, ""); put(v.actions, actions(p, false));
      }})), $(".s-pempty", root), rowsP);
    },
  };
};

VIEWS.session = (root, r) => {
  root.innerHTML = `
    <div class="sess-head"><a href="#/" class="btn">← overview</a><span class="title s-title"></span><span class="s-chip"></span>
      <div class="s-meter"></div><div class="s-actions"></div></div>
    <div class="s-plan"></div>
    <div class="card pad s-sumcard" hidden><div class="cardhead"><b>Summary</b><span><span class="sub s-stamp"></span> <button class="s-btn">Summarise</button></span></div>
      <div class="summary s-sum" style="margin-top:6px"></div></div>
    <div class="sess" style="margin-top:12px">
      <div><div class="card"><div class="stream s-stream"><div class="empty">loading…</div></div></div>
        <div class="say s-say" hidden><textarea class="s-text" placeholder="Message this worker. It arrives as your own turn. Ctrl+Enter sends."></textarea>
          <div class="bar2"><span class="sub s-hint"></span><button class="primary s-send">Send</button></div></div></div>
      <div class="side"><div class="card pad s-ctl" hidden><b>Controls</b>
          <div class="ctlrow"><label>budget, weighted tokens <input class="s-budget" inputmode="numeric" placeholder="e.g. 20M"></label><button class="s-setbudget">set</button></div>
          <div class="sub s-ctlhint"></div></div>
        <div class="card pad"><b>Session</b><dl class="s-info" style="margin-top:8px"></dl></div>
        <div class="card pad"><b>Subagents</b><div class="s-agents" style="margin-top:6px"><span class="sub">none</span></div></div></div>
    </div>`;
  const stream = $(".s-stream", root), tools = new Map();
  let sid, es = null, agentsRev = null, worker = null;

  function open(newSid) {
    if (es) es.close();
    sid = newSid; tools.clear(); agentsRev = null;
    if (!sid) { stream.innerHTML = `<div class="empty">No transcript yet.</div>`; return; }
    es = new EventSource(r.agent ? `/api/sessions/${sid}/agents/${r.agent}/stream` : `/api/sessions/${sid}/stream`);
    es.addEventListener("backlog", e => {
      const d = JSON.parse(e.data);
      stream.innerHTML = d.skipped || d.truncated ? `<div class="ev notice">earlier events are not shown</div>` : "";
      tools.clear();
      append(d.events, true);
    });
    es.addEventListener("events", e => append(JSON.parse(e.data), false));
  }

  function append(events, first) {
    const stick = first || stream.scrollHeight - stream.scrollTop - stream.clientHeight < 40;
    const frag = document.createDocumentFragment();
    for (const ev of events) { const el = event(ev); if (el) frag.appendChild(el); }
    stream.appendChild(frag);
    if (!stream.children.length) stream.innerHTML = `<div class="empty">Nothing yet.</div>`;
    if (stick) stream.scrollTop = stream.scrollHeight;
  }

  function event(ev) {
    const div = document.createElement("div");
    div.className = "ev " + ev.kind;
    const meta = t => `<div class="meta">${esc(t)} · ${esc(clock(ev.at))}</div>`;
    switch (ev.kind) {
      case "prompt": {
        const parts = ev.text.replace(/\r/g, "").split("\n# Brief\n");
        div.innerHTML = meta(ev.queued ? "prompt (queued)" : "prompt") + `<div class="body">${parts.length > 1
          ? `<details><summary>worker instructions</summary><pre>${esc(parts[0])}</pre></details>${md(parts.slice(1).join("\n# Brief\n"))}` : md(ev.text)}</div>`;
        return div;
      }
      case "message": div.innerHTML = meta(`message from ${ev.sender}`) + `<div class="body">${md(ev.text)}</div>`; return div;
      case "said": div.innerHTML = meta("assistant") + `<div class="body">${md(ev.text)}</div>`; return div;
      case "thinking": div.innerHTML = `<details><summary>thinking · ${esc(clock(ev.at))}</summary><pre>${esc(ev.text)}</pre></details>`; return div;
      case "tool":
        div.innerHTML = `<div class="tool"><div class="head"><b>${esc(ev.tool)}</b><span title="${esc(ev.summary)}">${esc(ev.summary)}</span><span class="sub">${esc(clock(ev.at))}</span></div>
          ${ev.input ? `<details><summary>input</summary><pre>${esc(ev.input)}</pre></details>` : ""}<div class="res"></div></div>`;
        tools.set(ev.id, {el: div, ev});
        return div;
      case "result": {
        const t = tools.get(ev.id), html = result(ev, t && t.ev);
        if (t) { const host = $(".res", t.el); host.className = "res" + (ev.error ? " error" : ""); host.innerHTML = html; return null; }
        div.innerHTML = `<div class="tool"><div class="head"><b>result</b></div><div class="res${ev.error ? " error" : ""}">${html}</div></div>`;
        return div;
      }
      case "turn": div.innerHTML = `<hr>turn ended${ev.ms ? ` · ${Math.round(ev.ms / 1000)} s` : ""}`; return div;
      case "compact": div.innerHTML = `<hr>context compacted`; return div;
      case "notice": div.textContent = ev.text; return div;
      case "recap": div.innerHTML = meta("Claude Code's recap") + `<div class="body">${md(ev.text)}</div>`; return div;
      case "error": div.innerHTML = meta("error") + `<div class="body">${esc(ev.text)}</div>`; return div;
    }
    return null;
  }

  function result(ev, tool) {
    const sub = ev.agentId ? ` · <a href="#/s/${sid}/a/${esc(ev.agentId)}">open the subagent's transcript ›</a>` : "";
    if (ev.denied) {
      let cmd = "";
      try { const inp = JSON.parse(tool?.input || "{}"); cmd = inp.command || inp.file_path || ""; } catch { cmd = tool?.input || ""; }
      return `<div class="denied"><div class="who">Refused by ${esc(ev.denied)}</div>${cmd ? `<pre>${esc(cmd)}</pre><button data-copytext="${esc(cmd)}">copy</button>` : ""}
        <details><summary>what it said</summary><pre>${esc(ev.text)}</pre></details></div>`;
    }
    const lines = (ev.text.match(/\n/g) || []).length + 1;
    return `<details><summary>${ev.error ? "error" : "result"} · ${lines} line${lines === 1 ? "" : "s"}</summary><pre>${esc(ev.text)}</pre></details>${sub}`;
  }

  async function loadAgents(rev) {
    if (!sid || r.agent || rev === agentsRev) return;
    agentsRev = rev;
    try {
      const list = await (await fetch(`/api/sessions/${sid}/agents`)).json();
      put($(".s-agents", root), list.length ? list.sort((a, b) => b.lastActivity - a.lastActivity).map(a =>
        `<a class="agent" href="#/s/${sid}/a/${esc(a.id)}">${esc(a.description || a.id)}<small>${esc(a.type || "agent")} · ${esc(M(a.weighted))} · ${esc(ago(a.lastActivity))}</small></a>`).join("")
        : `<span class="sub">none</span>`);
    } catch { agentsRev = null; }
  }

  async function send() {
    const box = $(".s-text", root), text = box.value.trim();
    if (!text || !worker) return;
    const btn = $(".s-send", root);
    btn.disabled = true; put($(".s-hint", root), "delivering…");
    try {
      const j = await post(`/api/workers/${worker}/say`, {text});
      box.value = ""; put($(".s-hint", root), `delivered as your turn; it continues as ${esc(j.id)}`);
    } catch (e) { put($(".s-hint", root), `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
    btn.disabled = false;
  }
  async function setBudget() {
    const raw = $(".s-budget", root).value.trim().toLowerCase().replace(/[, _]/g, "");
    const m = /^(\d+(?:\.\d+)?)([km]?)$/.exec(raw);
    if (!m || !worker) { put($(".s-ctlhint", root), "a number, like 20000000 or 20M"); return; }
    const tokens = Math.round(parseFloat(m[1]) * ({k: 1e3, m: 1e6}[m[2]] || 1));
    try { await post(`/api/workers/${worker}/budget`, {tokens}); $(".s-budget", root).value = ""; put($(".s-ctlhint", root), `budget set to ${esc(M(tokens))}; message it to continue if it stopped at the old one`); }
    catch (e) { put($(".s-ctlhint", root), `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
  }
  $(".s-setbudget", root).addEventListener("click", setBudget);
  $(".s-budget", root).addEventListener("keydown", e => { if (e.key === "Enter") setBudget(); });
  $(".s-send", root).addEventListener("click", send);
  $(".s-text", root).addEventListener("keydown", e => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); } });
  $(".s-btn", root).addEventListener("click", () => worker && summarise(worker));

  return {
    update(s) {
      let w = r.worker ? s.workers.find(x => x.name === r.worker) : s.workers.find(x => x.sessionId === r.sid);
      const p = !w && r.sid ? s.pilots.find(x => x.sessionId === r.sid) : null;
      const h = !w && !p ? s.history.find(x => (r.worker && x.name === r.worker) || (r.sid && x.sessionId === r.sid)) : null;
      worker = w ? w.name : null;
      const want = w ? w.sessionId : h ? h.sessionId : r.sid;
      if (want !== sid) open(want);
      const name = w ? w.name : p ? p.name : h ? `${h.name} (removed)` : (r.sid || "").slice(0, 8);
      put($(".s-title", root), esc(name) + (r.agent ? ` <span class="sub">› subagent ${esc(r.agent)}</span>` : ""));
      put($(".s-chip", root), w ? chip(w) : p ? `<span class="chip ${p.busy ? "working" : "other"}">${p.busy ? "● busy" : "idle"}</span>` : h ? chip(h) : "");
      put($(".s-meter", root), w ? meter(w.spend, w.budget) : p ? meter(p.spend, 0) : h ? meter(h.final?.spend, 0) : "");
      put($(".s-actions", root), w ? actions(w, true) : p ? actions(p, false) : "");
      const plan = (s.plans || []).find(x => x.sessionId === (p ? p.sessionId : r.sid));
      put($(".s-plan", root), plan && !r.agent ? planCard(plan, s) : "");
      $(".s-ctl", root).hidden = !w;
      const card = $(".s-sumcard", root);
      card.hidden = !w;
      if (w) {
        const sp = summaryParts(w.name, s);
        put($(".s-sum", root), sp.body); put($(".s-stamp", root), esc(sp.stamp)); $(".s-btn", root).disabled = sp.running;
      }
      const say = $(".s-say", root);
      say.hidden = !w || !!r.agent;
      if (w) {
        const busy = w.pid && (w.state === "working" || w.state === "blocked");
        $(".s-send", root).disabled = busy;
        if (busy && $(".s-hint", root)._html?.startsWith("delivered") !== true)
          put($(".s-hint", root), w.state === "working" ? "It is mid-turn. A message now would cut the turn off: wait, or use attach or remote."
            : "It is waiting on a prompt only attach or remote can answer.");
        else if (!busy && !$(".s-hint", root)._html) put($(".s-hint", root), "Its turn has ended: a message continues it with your words as a user turn.");
      }
      const info = w || h || p || {};
      const leg = (s.plans || []).flatMap(x => x.legs.map(l => ({...l, plan: x.title}))).find(l => l.leg === (w || h)?.name);
      const rows = [["flight plan", leg?.plan], ["leg goal", leg?.goal], ["done when", leg?.doneWhen],
        ["repo", info.repo], ["branch", info.branch], ["from", info.base], ["worktree", info.worktree], ["model", info.model || (w ? "account default" : "")],
        ["effort", info.effort], ["sessions", Array.isArray(info.sessions) ? info.sessions.length : info.sessions], ["pilot", info.pilot], ["after", (info.after || []).join(", ")],
        ["started", when(info.started)], ["removed", when(info.archivedAt)], ["Claude Code says", info.detail || info.final?.detail], ["session", sid]];
      put($(".s-info", root), rows.filter(([, v]) => v).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join(""));
      loadAgents(w ? w.rev : p ? p.rev : 0);
    },
    unmount() { if (es) es.close(); },
  };
};

VIEWS.history = root => {
  root.innerHTML = `<h2>History</h2><div class="card tablewrap"><table><thead><tr><th>Worker</th><th>Result</th><th>Branch</th><th class="num">Spend</th><th>Removed</th><th></th></tr></thead>
    <tbody class="s-rows"></tbody></table><div class="empty s-empty">Workers removed with <code>pilot rm</code> are kept here with their report, commits and spend.</div></div>`;
  const opened = new Set();
  root.addEventListener("click", e => {
    const tr = e.target.closest("tr[data-key]");
    if (!tr || e.target.closest("a")) return;
    opened.has(tr.dataset.key) ? opened.delete(tr.dataset.key) : opened.add(tr.dataset.key);
    render();
  });
  return {
    update(s) {
      put($(".s-rows", root), s.history.map(h => {
        const key = `${h.name}-${h.archivedAt}`, f = h.final || {};
        return `<tr class="click" data-key="${esc(key)}"><td><b>${esc(h.name)}</b><div class="sub">${esc(h.repo)}</div></td><td>${chip(h)}<div class="sub">${esc(f.detail || "")}</div></td>
          <td>${esc(h.branch)}<div class="sub">from ${esc(h.base || "")}</div></td><td class="num">${esc(M(f.spend?.weighted))}</td><td>${esc(when(h.archivedAt))}</td>
          <td>${h.sessionId ? `<a href="#/s/${esc(h.sessionId)}">conversation ›</a>` : ""}</td></tr>` +
          (opened.has(key) ? `<tr><td colspan="6"><div class="summary">${f.report ? md(f.report) : `<span class="sub">no report</span>`}</div>
            ${f.commits ? `<h2>Commits</h2><pre>${esc(f.commits)}</pre>` : ""}</td></tr>` : "");
      }).join(""));
      $(".s-empty", root).hidden = s.history.length > 0;
    },
  };
};

VIEWS.spend = root => {
  root.innerHTML = `<h2>Weighted tokens per day, all workers</h2><div class="card pad"><div class="chart s-chart"></div>
      <div class="sub">weighted = input + 1.25 × cache write + 0.1 × cache read + 5 × output, the ratios Sam's hooks use. Last 14 days, current and removed workers.</div></div>
    <h2>Per worker</h2><div class="card tablewrap"><table><thead><tr><th>Worker</th><th class="num">Weighted</th><th class="num">Budget</th><th class="num">Input</th>
      <th class="num">Cache write</th><th class="num">Cache read</th><th class="num">Output</th><th class="num">Sessions</th></tr></thead><tbody class="s-rows"></tbody></table></div>`;
  const chart = $(".s-chart", root);
  let bars = [];
  chart.addEventListener("mousemove", e => {
    const i = e.target.closest("[data-i]")?.dataset.i, tip = $(".tip", chart);
    if (i === undefined || !tip) { if (tip) tip.hidden = true; return; }
    const b = bars[i], box = chart.getBoundingClientRect();
    tip.innerHTML = `<b>${esc(b.label)}</b> · ${esc(M(b.total))} weighted` + b.parts.map(([n, v]) => `<div>${esc(n)}: ${esc(M(v))}</div>`).join("");
    tip.hidden = false;
    tip.style.left = Math.min(e.clientX - box.left + 12, box.width - tip.offsetWidth - 4) + "px";
    tip.style.top = (e.clientY - box.top - 10) + "px";
  });
  chart.addEventListener("mouseleave", () => { const tip = $(".tip", chart); if (tip) tip.hidden = true; });
  return {
    update(s) {
      const all = [...s.workers.map(w => ({name: w.name, spend: w.spend, budget: w.budget, sessions: w.sessions})),
                   ...s.history.map(h => ({name: `${h.name} (removed)`, spend: h.final?.spend, budget: null, sessions: (h.sessions || []).length}))];
      const days = [];
      for (let i = 13; i >= 0; i--) {
        const d = new Date(); d.setDate(d.getDate() - i);
        const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
        const parts = all.map(x => [x.name, x.spend?.days?.[key] || 0]).filter(([, v]) => v > 0).sort((a, b) => b[1] - a[1]);
        days.push({key, label: d.toLocaleDateString([], {weekday: "short", day: "numeric"}), total: parts.reduce((t, [, v]) => t + v, 0), parts});
      }
      bars = days;
      const W = Math.max(320, (chart.clientWidth || 708) - 8), H = 220, L = 44, B = 24, T = 8, max = Math.max(1, ...days.map(d => d.total));
      const step = 10 ** Math.floor(Math.log10(max)), top = Math.ceil(max / step) * step, bw = (W - L) / days.length;
      const y = v => T + (H - T - B) * (1 - v / top);
      const grid = [0, .25, .5, .75, 1].map(f => `<line class="gridline" x1="${L}" x2="${W}" y1="${y(top * f)}" y2="${y(top * f)}"/><text x="${L - 6}" y="${y(top * f) + 4}" text-anchor="end">${M(top * f)}</text>`).join("");
      const marks = days.map((d, i) => {
        const x = L + i * bw + bw * 0.18, w = bw * 0.64, yy = y(d.total), base = y(0), r = Math.min(4, (base - yy) / 2, w / 2);
        const bar = d.total ? `<path class="barmark" d="M${x},${base} V${yy + r} Q${x},${yy} ${x + r},${yy} H${x + w - r} Q${x + w},${yy} ${x + w},${yy + r} V${base} Z"/>` : "";
        const lbl = (days.length <= 14 || i % 2 === 0) ? `<text x="${x + w / 2}" y="${H - 6}" text-anchor="middle">${esc(d.label)}</text>` : "";
        return `<rect class="hit" data-i="${i}" x="${L + i * bw}" y="${T}" width="${bw}" height="${H - T - B}"/>${bar}${lbl}`;
      }).join("");
      put(chart, `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="weighted tokens per day">${grid}<line class="baseline" x1="${L}" x2="${W}" y1="${y(0)}" y2="${y(0)}"/>${marks}</svg><div class="tip" hidden></div>`);
      const t = x => x.spend?.tokens || {};
      put($(".s-rows", root), all.length ? all.sort((a, b) => (b.spend?.weighted || 0) - (a.spend?.weighted || 0)).map(x => `<tr><td>${esc(x.name)}</td>
        <td class="num">${esc(M(x.spend?.weighted))}</td><td class="num">${x.budget ? esc(M(x.budget)) + ` (${Math.round(100 * (x.spend?.weighted || 0) / x.budget)}%)` : "-"}</td>
        <td class="num">${esc(M(t(x).input_tokens))}</td><td class="num">${esc(M(t(x).cache_creation_input_tokens))}</td><td class="num">${esc(M(t(x).cache_read_input_tokens))}</td>
        <td class="num">${esc(M(t(x).output_tokens))}</td><td class="num">${esc(x.sessions ?? "")}</td></tr>`).join("")
        : `<tr><td colspan="8" class="sub">No workers yet.</td></tr>`);
    },
  };
};

VIEWS.launch = root => {
  root.innerHTML = `<h2>Launch a pilot</h2><div class="card pad launch">
      <label>Project <select class="s-proj"><option>loading…</option></select></label>
      <div class="sub s-projnote"></div>
      <div><b>Session</b><div class="s-sessions sessions"></div></div>
      <div class="opts"><b>Where</b>
        <label><input type="radio" name="where" value="terminal" checked> a new Windows Terminal tab: interactive, you type into it</label>
        <label><input type="radio" name="where" value="background"> in the background: reach it with attach or Remote Control</label></div>
      <label>First message (optional)<textarea class="s-msg" placeholder="What this flight is for. Sent as the session's first prompt."></textarea></label>
      <label class="inline"><input type="checkbox" class="s-aspilot" checked> start it as the pilot (<code>/pilot:pilot</code>)</label>
      <div class="bar2"><span class="sub s-result"></span><button class="primary s-go">Launch</button></div></div>`;
  const sel = $(".s-proj", root), list = $(".s-sessions", root);
  let projects = [];
  async function loadSessions() {
    const p = projects.find(x => x.path === sel.value);
    put($(".s-projnote", root), p ? (p.trusted ? `workspace trusted · last session ${esc(ago(p.lastStart / 1000))}` : `<span style="color:var(--crit)">workspace trust not accepted yet: only a terminal tab can start here</span>`) : "");
    list.innerHTML = `<span class="sub">loading…</span>`;
    const ss = await (await fetch(`/api/projects/sessions?path=${encodeURIComponent(sel.value)}`)).json().catch(() => []);
    list.innerHTML = `<label><input type="radio" name="sess" value="" checked> <b>a new session</b></label>` + (Array.isArray(ss) ? ss : []).map(x =>
      `<label title="${esc(x.sessionId)}"><input type="radio" name="sess" value="${esc(x.sessionId)}"> resume <b>${esc(x.title)}</b>
        <span class="sub">${esc(ago(x.updated))}${x.live ? " · running now: resuming starts a copy" : ""}</span></label>`).join("");
  }
  (async () => {
    projects = await (await fetch("/api/projects")).json().catch(() => []);
    const fromHash = new URLSearchParams(location.hash.split("?")[1] || "").get("path");
    sel.innerHTML = projects.map(p => `<option value="${esc(p.path)}"${p.path === fromHash ? " selected" : ""}>${esc(p.path)}${p.trusted ? "" : " (not trusted)"}</option>`).join("");
    if (projects.length) loadSessions();
  })();
  sel.addEventListener("change", loadSessions);
  $(".s-go", root).addEventListener("click", async () => {
    const btn = $(".s-go", root);
    btn.disabled = true; put($(".s-result", root), "launching…");
    try {
      const j = await post("/api/launch", {path: sel.value, resume: $('input[name="sess"]:checked', root)?.value || null,
        where: $('input[name="where"]:checked', root).value, message: $(".s-msg", root).value, asPilot: $(".s-aspilot", root).checked});
      put($(".s-result", root), j.where === "terminal" ? "opened in a new Windows Terminal tab"
        : `started in the background as ${esc(j.id)}: <button data-copy="claude attach ${esc(j.id)}">copy attach</button> or open it from Overview`);
    } catch (e) { put($(".s-result", root), `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
    btn.disabled = false;
  });
  return {update() {}};
};

// ------------------------------------------------------------------ routing

function parse() {
  const h = location.hash.replace(/^#/, "") || "/";
  let m;
  if ((m = /^\/w\/([a-z0-9-]+)$/.exec(h))) return {view: "session", worker: m[1]};
  if ((m = /^\/s\/([0-9a-f-]{36})(?:\/a\/([0-9a-f]+))?$/.exec(h))) return {view: "session", sid: m[1], agent: m[2]};
  if (h === "/history") return {view: "history"};
  if (h === "/spend") return {view: "spend"};
  if (h.startsWith("/launch")) return {view: "launch"};
  return {view: "overview"};
}

let current = null;
function render() {
  renderTop();
  const r = parse(), key = JSON.stringify(r);
  if (!current || current.key !== key) {
    if (current && current.view.unmount) current.view.unmount();
    const root = $("#view");
    root.innerHTML = ""; root._html = undefined;
    current = {key, view: VIEWS[r.view](root, r)};
    window.scrollTo(0, 0);
  }
  if (S) current.view.update(S);
}

async function summarise(key) {
  delete sumErr[key];
  try { await post(`/api/summary/${key}`); } catch (e) { sumErr[key] = `Summary failed: ${e.message}`; }
  render();
}

document.addEventListener("click", async e => {
  const t = e.target.closest("button, a, [data-href]");
  if (!t) return;
  if (t.id === "bell") { $("#panel").hidden = !$("#panel").hidden; return; }
  if (t.id === "readall") { await post("/api/notifications/read").catch(() => {}); return; }
  if (t.id === "desk") { if ("Notification" in window) await Notification.requestPermission(); renderTop(); return; }
  if (t.dataset.copy || t.dataset.copytext) {
    const text = t.dataset.copy || t.dataset.copytext, was = t.textContent;
    navigator.clipboard?.writeText(text);
    t.textContent = "copied"; setTimeout(() => { t.textContent = was; }, 1500);
    return;
  }
  if (t.dataset.stop) {
    if (!confirm(`Stop ${t.dataset.stop}? Its conversation is kept: claude attach, or a message from here, resumes it.`)) return;
    try { await post(`/api/workers/${t.dataset.stop}/stop`); } catch (err) { alert(err.message); }
    return;
  }
  if (t.dataset.sum) { summarise(t.dataset.sum); return; }
  if (t.dataset.note) {
    post("/api/notifications/read", {ids: [t.dataset.note]}).catch(() => {});
    $("#panel").hidden = true;
  }
  if (t.tagName === "A" || t.tagName === "BUTTON") return;
  if (t.dataset.href && !getSelection().toString()) location.hash = t.dataset.href;
});
document.addEventListener("click", e => { if (!e.target.closest(".bellwrap")) $("#panel").hidden = true; }, true);
window.addEventListener("hashchange", render);
setInterval(render, 30000);        // only the "5m ago" texts move between states
render();
connect();
