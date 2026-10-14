"use strict";
// A script error shows on the page rather than only in a console nobody has open. The same error again
// counts up on its line instead of adding another.
const shown = new Map();
function oops(text) {
  const box = document.getElementById("errors");
  if (!box) return;
  text = String(text);
  const seen = shown.get(text);
  if (seen) { seen.n++; seen.el.lastChild.textContent = ` (${seen.n} times)`; return; }
  const el = document.createElement("p");
  el.className = "warnline";
  el.textContent = `Dashboard error: ${text}`;
  el.appendChild(document.createElement("span"));
  box.appendChild(el);
  shown.set(text, {el, n: 1});
}
window.addEventListener("error", e => {
  // Chrome reports a ResizeObserver pass that left work for the next frame as a page error. It is harmless,
  // and it is the browser's own: nothing on this page uses a ResizeObserver.
  if (/^ResizeObserver loop/.test(e.message || "")) return;
  oops(`${e.message} (${String(e.filename || "").split("/").pop()}:${e.lineno})`);
});
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
// A <details data-k> keeps the reader's open or closed choice when put() rebuilds it. Only a choice that
// differs from its default (data-open="1" when open by default) is kept, so the default still moves: a plan
// that lands folds away unless the reader opened it.
const opened = new Map();
document.addEventListener("toggle", e => {
  const d = e.target, k = d.dataset?.k;
  if (!k) return;
  if (d.open === (d.dataset.open === "1")) opened.delete(k); else opened.set(k, d.open);
}, true);
function put(el, html) {
  if (!el || el._html === html) return;
  if (dragging && el.contains(dragging.item)) return;   // a flight being dragged: the drop redraws it
  const f = document.activeElement;      // never wipe what the reader is typing: the next render catches up
  if (f && f !== document.body && el.contains(f) && f.matches("input, textarea, select")) return;
  el.innerHTML = html; el._html = html;
  for (const d of el.querySelectorAll("details[data-k]")) if (opened.has(d.dataset.k)) d.open = opened.get(d.dataset.k);
}
// Copying text with a report's placeholders in it copies what they stand for: a command runs as pasted.
document.addEventListener("copy", e => {
  const sel = getSelection();
  if (!sel.rangeCount) return;
  const frag = sel.getRangeAt(0).cloneContents();
  const phs = frag.querySelectorAll("abbr.ph[data-expand]");
  if (!phs.length) return;
  for (const a of phs) a.replaceWith(a.dataset.expand);
  const box = document.createElement("div");           // innerText keeps line breaks only for laid-out text
  box.style.cssText = "position:fixed;left:-99999px;top:0;white-space:pre-wrap";
  box.append(frag); document.body.append(box);
  e.clipboardData.setData("text/plain", box.innerText);
  box.remove(); e.preventDefault();
});
function local(k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch { return null; } }
async function post(url, body) {
  const r = await fetch(url, {method: "POST", headers: {"X-Pilot": "1", "Content-Type": "application/json"}, body: JSON.stringify(body || {})});
  const j = await r.json().catch(() => ({}));
  // The page comes from disk on every load, the server's code only when it starts: a button newer than the
  // server meets a route it has never heard of.
  if (r.status === 404 && j.error === "not found")
    throw new Error(`This dashboard started before ${url} existed, so its server does not know it. Restart it (python pilot.py dashboard) and try again.`);
  if (!r.ok) throw Object.assign(new Error(j.error || `HTTP ${r.status}`), {flying: j.flying});   // an archive refused names its legs flying
  return j;
}

// ------------------------------------------------------------------ a small Markdown: enough for reports

// A report's line breaks are kept; `soft` joins a paragraph's lines, for prose wrapped at a fixed width.
// A placeholder the text defines, "`<pv>` = `C:\...\pdf-viewer`", shows its value on hover wherever it is used,
// and copies as that value.
function md(src, soft) {
  const out = [], lines = String(src ?? "").replace(/\r/g, "").split("\n");
  const defs = new Map();
  for (const m of String(src ?? "").matchAll(/`(<[A-Za-z][\w.-]*>)`\s*(?:=|:=|is)\s*`([^`\n]+)`/g)) defs.set(esc(m[1]), m[2]);
  const ph = defs.size && new RegExp([...defs.keys()].map(k => k.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|"), "g");
  const placeholders = h => !ph ? h : h.replace(ph, (k, at) => /^<\/code>\s*(?:=|:=|is)\s*<code>/.test(h.slice(at + k.length))
    ? `<abbr class="ph" title="${esc(defs.get(k))}">${k}</abbr>`
    : `<abbr class="ph" title="${esc(defs.get(k))}" data-expand="${esc(defs.get(k))}">${k}</abbr>`);
  const inline = s => placeholders(esc(s).replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>")
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>'));
  // Lists nest by indent: `root` is the outermost list, `stack` the lists open inside it, innermost last.
  let para = [], root = null, stack = [];
  const listHtml = L => `<${L.t}>${L.items.map(x => `<li>${inline(x.text)}${x.subs.map(listHtml).join("")}</li>`).join("")}</${L.t}>`;
  const flushPara = () => { if (para.length) out.push(`<p>${para.map(inline).join(soft ? " " : "<br>")}</p>`); para = []; };
  const flushList = () => { if (root) out.push(listHtml(root)); root = null; stack = []; };
  const item = (text, t, n) => {
    while (stack.length && stack.at(-1).n > n) stack.pop();
    if (stack.length && stack.at(-1).n === n && stack.at(-1).t !== t) stack.pop();
    if (!stack.length) flushList();
    let top = stack.at(-1);
    if (!top || top.n < n) {
      const L = {t, n, items: []};
      if (top) top.items.at(-1).subs.push(L); else root = L;
      stack.push(top = L);
    }
    top.items.push({text, subs: []});
  };
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
      flushPara();
      item(m[1], /^\s*\d/.test(l) ? "ol" : "ul", /^\s*/.exec(l)[0].replace(/\t/g, "    ").length);
    } else if (!l.trim()) { flushPara(); flushList(); }
    else if (root && /^\s{2,}/.test(l)) stack.at(-1).items.at(-1).text += " " + l.trim();
    else { flushList(); para.push(l); }
  }
  flushPara(); flushList();
  return `<div class="md">${out.join("")}</div>`;
}

// ------------------------------------------------------------------ terminal colours

// ANSI escapes in tool and shell output become colour: bold, dim, italic, underline, and 16, 256 and 24-bit
// colours. Other escapes (cursor moves, erases, OSC titles and links) are dropped.
const ANSI16 = ["#1e1e1e", "#cd3131", "#0dbc79", "#c8b400", "#2472c8", "#bc3fbc", "#11a8cd", "#cccccc",
                "#767676", "#f14c4c", "#23d18b", "#e5e510", "#3b8eea", "#d670d6", "#29b8db", "#ffffff"];
function ansi256(n) {
  if (n < 16) return ANSI16[n];
  if (n >= 232) { const v = 8 + (n - 232) * 10; return `rgb(${v},${v},${v})`; }
  n -= 16;
  return `rgb(${[Math.floor(n / 36), Math.floor(n / 6) % 6, n % 6].map(x => x ? 55 + x * 40 : 0)})`;
}
function ansi(src) {
  const s = String(src ?? "").replace(/\x1b\][^\x07\x1b]*(\x07|\x1b\\)/g, "").replace(/\x1b\[[0-9;?]*[A-Za-ln-z]/g, "");
  if (!s.includes("\x1b[")) return esc(s);
  const parts = s.split(/\x1b\[([0-9;]*)m/);
  let out = "", st = {}, open = false;
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 0) { out += esc(parts[i]); continue; }
    const codes = parts[i] === "" ? [0] : parts[i].split(";").map(Number);
    for (let j = 0; j < codes.length; j++) {
      const c = codes[j];
      if (c === 0) st = {};
      else if (c === 1) st.b = 1; else if (c === 2) st.d = 1; else if (c === 3) st.i = 1; else if (c === 4) st.u = 1;
      else if (c === 22) { delete st.b; delete st.d; } else if (c === 23) delete st.i; else if (c === 24) delete st.u;
      else if (c >= 30 && c <= 37) st.fg = ANSI16[c - 30]; else if (c >= 90 && c <= 97) st.fg = ANSI16[c - 82];
      else if (c >= 40 && c <= 47) st.bg = ANSI16[c - 40]; else if (c >= 100 && c <= 107) st.bg = ANSI16[c - 92];
      else if (c === 39) delete st.fg; else if (c === 49) delete st.bg;
      else if ((c === 38 || c === 48) && codes[j + 1] === 5) { st[c === 38 ? "fg" : "bg"] = ansi256(codes[j + 2]); j += 2; }
      else if ((c === 38 || c === 48) && codes[j + 1] === 2) { st[c === 38 ? "fg" : "bg"] = `rgb(${codes.slice(j + 2, j + 5)})`; j += 4; }
    }
    if (open) out += "</span>";
    const css = [st.fg && `color:${st.fg}`, st.bg && `background:${st.bg}`, st.b && "font-weight:700", st.d && "opacity:.7",
                 st.i && "font-style:italic", st.u && "text-decoration:underline"].filter(Boolean).join(";");
    open = !!css;
    if (open) out += `<span style="${css}">`;
  }
  return out + (open ? "</span>" : "");
}

// A console screen's runs, [text, colour]: the low nibble is the ink, the high the paper, with the console's
// bit order (1 blue, 2 green, 4 red, 8 bright) against ANSI's (1 red, 2 green, 4 blue). Light grey on black,
// the console's default, takes the page's own colours.
const conColour = i => ANSI16[((i & 1) << 2) | (i & 2) | ((i & 4) >> 2) | (i & 8)];
const screenHtml = rows => rows.map(row => row.map(([text, a]) => {
  const ink = a & 15, paper = a >> 4, css = [ink !== 7 && `color:${conColour(ink)}`, paper && `background:${conColour(paper)}`].filter(Boolean).join(";");
  return css ? `<span style="${css}">${esc(text)}</span>` : esc(text);
}).join("")).join("\n");

// ------------------------------------------------------------------ shared pieces

// Claude Code keeps a job's last state after its session ends, so a worker whose turn ended on its budget stays
// "blocked" for good, even once it is refuelled, has reported DONE and has its checklist complete. Ended, DONE and
// complete, it waits on no one.
function waitsOnYou(w) {
  return w.state === "blocked" && !(!w.pid && w.reportHead === "DONE" && !openItems(w.checklist || []).length);
}

function stateOf(w) {
  if (w.state === "working") return {cls: "working", label: "● working", stroke: "var(--fill)", tint: "var(--tint-fill)"};
  if (waitsOnYou(w)) return {cls: "blocked", label: "waiting on you", stroke: "var(--warn)", tint: "var(--tint-warn)"};
  if (w.state === "done" || w.state === "blocked" || w.final) {
    const head = w.reportHead || w.final?.reportHead, open = openItems(w.checklist || w.final?.checklist).length;
    if (head === "FAILED") return {cls: "failed", label: "✗ failed", stroke: "var(--crit)", tint: "var(--tint-crit)"};
    if (head === "BLOCKED") return {cls: "blocked", label: "blocked: needs you", stroke: "var(--warn)", tint: "var(--tint-warn)"};
    // DONE with checklist items still open has not landed: the pilot sends it back or makes them a leg.
    if (head === "DONE" && open) return {cls: "blocked", label: `DONE, ${open} item${open > 1 ? "s" : ""} open`, stroke: "var(--warn)", tint: "var(--tint-warn)"};
    // Stopped after the plan's log last named it: its pilot has not looked, so nothing will happen until it does.
    if (head === "DONE" && w.unread) return {cls: "blocked", label: "✓ done · pilot hasn't looked", stroke: "var(--warn)", tint: "var(--tint-warn)"};
    if (head === "DONE") return {cls: "done", label: "✓ done", stroke: "var(--good)", tint: "var(--tint-good)"};
    return {cls: "other", label: "turn ended", stroke: "var(--axis)", tint: "var(--surface)"};
  }
  if (w.lastError) return {cls: "failed", label: "✗ error", stroke: "var(--crit)", tint: "var(--tint-crit)"};
  return {cls: "other", label: w.state || "unknown", stroke: "var(--axis)", tint: "var(--surface)"};
}
const chip = w => { const s = stateOf(w); return `<span class="chip ${s.cls}" title="${esc(w.detail || "")}">${esc(s.label)}</span>`; };

// A session's checklist (its task list): how much is done, and what it is on now.
const openItems = items => (items || []).filter(i => i.status !== "completed");
function checkLine(items) {
  if (!items?.length) return "";
  const done = items.length - openItems(items).length, now = items.find(i => i.status === "in_progress");
  return `<div class="checkline" title="${esc(items.map(i => `${i.status === "completed" ? "✓" : i.status === "in_progress" ? "●" : "○"} ${i.subject}`).join("\n"))}">
    <span class="clbar"><i style="width:${(100 * done / items.length).toFixed(0)}%"></i></span>${done}/${items.length}${now ? ` · ${esc(now.activeForm || now.subject)}` : ""}</div>`;
}
function checkList(items) {
  return items.map(i => `<li class="${i.status}"><span>${i.status === "completed" ? "✓" : i.status === "in_progress" ? "●" : "○"}</span>
    <span>${esc(i.status === "in_progress" ? i.activeForm || i.subject : i.subject)}</span></li>`).join("");
}

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
  if (waitsOnYou(w) && w.detail) return `<span class="doing"><b>needs you:</b> ${esc(w.detail)}</span>`;
  if (w.state === "working" && w.now) return w.now.kind === "tool"
    ? `<span class="doing"><b>${esc(w.now.tool)}</b> ${esc(w.now.summary || w.now.text || "")}</span>` : `<span class="doing">${esc(w.now.text)}</span>`;
  if (w.detail) return `<span class="doing">${esc(w.detail)}</span>`;
  return w.now ? `<span class="doing">${esc(w.now.summary || w.now.text || "")}</span>` : `<span class="doing">nothing yet</span>`;
}

function actions(w, isWorker) {
  const a = [];
  if (w.id) a.push(`<button data-copy="claude attach ${esc(w.id)}" title="Copy the command that opens this session in a terminal">attach</button>`);
  if (w.remote) a.push(`<a class="btn" href="${esc(w.remote)}" target="_blank" rel="noopener" title="Talk to it from claude.ai or your phone (Remote Control)">remote ↗</a>`);
  if (isWorker && w.budget && (w.spend?.weighted || 0) >= 0.75 * w.budget) a.push(fillButton(w));
  if (isWorker && w.pid && w.state !== "gone") a.push(`<button class="danger" data-stop="${esc(w.name)}">stop</button>`);
  return `<div class="actions">${a.join("")}</div>`;
}

// The controller's refuel, which needs no watchtower: one more tank, the leg's first budget, past whatever is
// higher of its budget and its spend, rounded up to a whole million.
function fillTo(w) {
  const tank = w.budget0 || 10e6;
  return Math.ceil((Math.max(w.budget || 0, w.spend?.weighted || 0) + tank) / 1e6) * 1e6;
}
function fillButton(w) {
  const to = fillTo(w);
  return `<button class="fill" data-fill="${esc(w.name)}" data-to="${to}" title="${esc(`Raises ${w.name}'s budget from ${M(w.budget)} to ${M(to)}: `
    + `one more tank of its first budget (${M(w.budget0 || 10e6)}) past what it has spent. If its turn has ended, you are asked whether to tell it to carry on.`)}">⛽ refuel to ${M(to)}</button>`;
}

// ------------------------------------------------------------------ account usage, as the status line shows it

// The same rules as ~/.claude/statusline/Program.cs, so the two never disagree. Peak is weekdays 13:00-19:00
// UTC. A window's burn is projected forward from its start: the 5-hour one on the wall clock, the 7-day one
// over working hours only (07:30-21:00 local), since nothing is spent overnight. The gauge says whether the
// window runs dry before it resets (▰▱▱), only just makes it (▰▰▱, under a fifth of the window to spare),
// or is comfortable (▰▰▰). The 5-hour window goes past red when it runs dry within 15 minutes and the reset
// is further off than that.
const hhmm = ts => new Date(ts * 1000).toLocaleTimeString([], {hour: "2-digit", minute: "2-digit", hour12: false});
const dayhhmm = ts => new Date(ts * 1000).toLocaleString([], {weekday: "short", hour: "2-digit", minute: "2-digit", hour12: false});
const countdown = s => s < 3600 ? `${Math.floor(s / 60)}m` : s < 86400 ? `${Math.floor(s / 3600)}h${Math.floor(s % 3600 / 60)}m` : `${Math.floor(s / 86400)}d${Math.floor(s % 86400 / 3600)}h`;

function peakHtml(now) {
  const d = new Date(now * 1000), t13 = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), 13) / 1000, t19 = t13 + 6 * 3600;
  const dow = d.getUTCDay() || 7, weekday = dow <= 5, inPeak = weekday && now >= t13 && now < t19;
  const secs = inPeak ? t19 - now : weekday && now < t13 ? t13 - now : t13 + ({5: 3, 6: 2, 7: 1}[dow] || 1) * 86400 - now;
  const near = secs < 3600;
  const [icon, cls, what] = inPeak ? (near ? ["🌇", "warn", "peak ends in"] : ["🔥", "crit", "peak hours, ending in"])
    : (near ? ["🌅", "warn", "peak starts in"] : ["🌙", "good", "off-peak; peak starts in"]);
  return `<span class="usage-peak u-${cls}" title="${esc(`${what} ${countdown(secs)}. Peak is weekdays 13:00-19:00 UTC, when the 5-hour window drains faster.`)}">${icon} ${countdown(secs)}</span>`;
}

const atLocal = (ts, h, m, days = 0) => { const d = new Date(ts * 1000); d.setDate(d.getDate() + days); d.setHours(h, m, 0, 0); return d.getTime() / 1000; };
const offHours = ts => { const d = new Date(ts * 1000), h = d.getHours() + d.getMinutes() / 60; return h >= 21 || h < 7.5; };
const nextWorkStart = ts => atLocal(ts, 7, 30) > ts ? atLocal(ts, 7, 30) : atLocal(ts, 7, 30, 1);
function workSeconds(from, to) {
  let total = 0, t = from;
  while (t < to) {
    if (offHours(t)) { t = Math.min(nextWorkStart(t), to); continue; }
    const end = Math.min(atLocal(t, 21, 0), to);
    total += end - t; t = end;
  }
  return total;
}
function addWorkSeconds(from, need, cap) {
  let t = from;
  while (need > 0 && t < cap) {
    if (offHours(t)) { t = nextWorkStart(t); continue; }
    const chunk = atLocal(t, 21, 0) - t;
    if (chunk >= need) return t + Math.round(need);
    need -= chunk; t += chunk;
  }
  return Math.min(t, cap);
}
function exhaustAt(five, start, now, used) {
  if (used <= 0) return 0;
  if (five) return now > start ? now + Math.round((100 - used) / (used / (now - start))) : 0;
  const on = workSeconds(start, now);
  return on > 0 ? addWorkSeconds(now, (100 - used) / (used / on), now + 30 * 86400) : 0;
}

function usageWindow(key, w, now) {
  const five = key === "five_hour", span = five ? 5 * 3600 : 7 * 86400, fmt = five ? hhmm : dayhhmm;
  const icon = five ? "⏳" : "📅", name = five ? "5-hour window" : "7-day window";
  const used = w.used_percentage ?? 0, pct = Math.round(used);
  const reset = typeof w.resets_at === "number" ? w.resets_at : (Date.parse(w.resets_at) / 1000 || null);
  if (reset && reset <= now)
    return `<div class="limit meter" title="${esc(`${name}: reset at ${fmt(reset)}; no figures since`)}"><div class="lbl"><span class="u-good">${icon} reset</span><span>🔄 ${esc(fmt(reset))}</span></div>
      <div class="track"><div class="bar" style="width:0"></div></div></div>`;
  let gauge = null, critical = false, dry = 0;
  if (reset && pct >= 10) {
    dry = exhaustAt(five, reset - span, now, used);
    if (dry > now) {
      gauge = dry <= reset ? {meter: "▰▱▱", cls: "crit", out: true} : dry - reset < span / 5 ? {meter: "▰▰▱", cls: "warn"} : {meter: "▰▰▰", cls: "good"};
      critical = five && dry <= now + 900 && reset > now + 900;
    }
  }
  const cls = critical ? "alarm" : gauge?.out || pct >= 80 ? "crit" : pct >= 50 ? "warn" : "good";
  const tip = `${name}: ${pct}% used` + (reset ? `, resets ${fmt(reset)}` : "") + (gauge ? `. At this rate it runs dry ${fmt(dry)}, ` +
    (gauge.out ? "before it resets" : gauge.cls === "warn" ? "only just after it resets" : "well after it resets") : "") +
    (five ? "." : ". Projected over working hours, 07:30-21:00.");
  return `<div class="limit meter" title="${esc(tip)}"><div class="lbl"><span class="u-${cls}">${icon} ${pct}%</span>
      <span>${gauge ? `<span class="u-${critical ? "alarm" : gauge.cls}">${gauge.meter} ${esc(fmt(dry))}</span> ` : ""}${reset ? `🔄 ${esc(fmt(reset))}` : ""}</span></div>
    <div class="track"><div class="bar ${cls === "good" ? "" : cls}" style="width:${Math.min(100, used).toFixed(1)}%"></div></div></div>`;
}

function limitsHtml(l) {
  const now = Math.floor(Date.now() / 1000);
  if (!l || !l.windows) return peakHtml(now) + `<span class="sub" title="The status line writes ~/.claude/rate-limits.json whenever Claude Code hands it the account's windows (see README).">no usage figures yet</span>`;
  const old = l.writtenAt && now - l.writtenAt > 15 * 60;
  return peakHtml(now) + ["five_hour", "seven_day"].filter(k => l.windows[k]).map(k => usageWindow(k, l.windows[k], now)).join("")
    + (old ? `<span class="sub" title="The figures come from the last status line repaint in any session">as of ${esc(hhmm(l.writtenAt))}</span>` : "");
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

const NOTE_ICON = {waiting: "🟡", done: "✅", failed: "❌", died: "❌", budget: "⚠️", fuel: "⛽", quiet: "💤", turn: "•"};
function renderTop() {
  const r = parse();
  for (const a of document.querySelectorAll("nav a")) a.classList.toggle("on", a.dataset.nav === (r.view === "session" ? "" : r.view));
  if (!S?.workers) return;     // not loaded yet: an older server sends "{}" before its first snapshot
  // Whether a refuel can be approved by the watchtower right now, or has to come to you.
  // Refuelling's switch, which is not the autopilot's: auto lets the watchtower approve a refuel within its
  // gates (held while the account has no headroom), manual sends every refuel to the tower.
  const rf = S.refuel || {}, manual = rf.mode === "manual", held = !manual && !rf.ok;
  const fuel = S.refuel ? `<button class="fuel ${manual ? "manual" : held ? "held" : "auto"}" data-refuel="${manual ? "auto" : "manual"}" title="${esc(manual
    ? "Refuelling: manual. Every refuel request comes to the tower: you, or the autopilot while it is on. Click for auto, where the watchtower may approve them."
    : held ? `Refuelling: auto, but held now: ${(rf.why || []).join("; ")}. Until there is headroom, refuels come to the tower. Click for manual.`
    : `Refuelling: auto. The watchtower may approve a refuel when the reason and the worker's progress justify it (${(rf.why || []).join("; ")}). Click for manual.`)}">
    ⛽ refuel: ${manual ? "manual" : held ? "auto · held" : "auto"}</button>` : "";
  put($("#limits"), limitsHtml(S.limits) + fuel);
  // The autopilot's switch: off, starting, standby or working, and how many decisions it has made since on.
  const ap = S.autopilot || {}, apState = !ap.enabled ? "off" : !ap.live ? (Date.now() / 1000 - (ap.since || 0) < 90 ? "starting" : "asleep, wakes for items") : ap.status === "busy" ? "deciding" : "standby";
  put($("#autopilot"), `<button class="ap ${ap.enabled ? "on" : ""}" data-autopilot="${ap.enabled ? "off" : "on"}" title="${esc(ap.enabled
      ? `On since ${when(ap.since)}. It decides the tower's items in your place: all but irreversible. ${ap.decisions?.length || 0} decision(s) so far; the Black box has them. Click to switch it off.`
      : "Off. Switch it on when you leave: an Opus 5.5 session decides what waits on you, all but irreversible (never pushes, merges, deletes, or sets a new objective), each step checked by auto mode's classifier.")}">
    ✈ autopilot: ${apState}${ap.enabled && ap.decisions?.length ? ` · ${ap.decisions.length}` : ""}</button>`);
  const over = S.workers.filter(w => w.overBudget);
  // A session on a terminal prompt counts only if it is a pilot (it has a flight plan); a plan, only while an item
  // in it is unanswered: answers sent from here are with the pilot until it takes them out of the plan.
  const waiting = [...S.pilots.filter(p => p.status === "waiting" && (S.plans || []).some(x => x.sessionId === p.sessionId))
                     .map(p => ({name: `pilot ${p.name}`, href: `#/s/${p.sessionId}`})),
                   ...(S.plans || []).filter(p => unanswered(p).length).map(p => ({name: p.title, href: `#/s/${p.sessionId}`})),
                   ...S.workers.filter(waitsOnYou).map(w => ({name: w.name, href: `#/w/${w.name}`}))];
  // S.stale: pilot.py changed since this server started. A snapshot without refuel's mode is from a server
  // older than that check.
  const stale = S.stale || (S.refuel && !("mode" in S.refuel));
  const lock = S.daemonLock;
  put($("#alerts"),
    (lock ? `<p class="warnline">⚠ Claude Code's daemon lock is stale: ${esc(lock.why)}. No worker or background session can start until
      it goes. <button class="s-clearlock" data-pid="${esc(lock.pid)}" data-ft="${esc(lock.procStartFt)}">Remove the stale lock</button>
      <span class="sub">${esc(lock.path)}</span></p>` : "") +
    (stale ? `<p class="warnline amber">This dashboard is running older code than <code>pilot.py</code> on disk, so newer buttons may answer "not found". ${S.restartable
      ? `<button class="s-restart">Restart now</button> or press R in its terminal.` : `Restart it: <code>pilot dashboard</code>.`}</p>` : "") +
    (over.length ? `<p class="warnline">⚠ Over budget: ${over.map(w => `<a href="#/w/${esc(w.name)}">${esc(w.name)}</a> ${fillButton(w)}`).join(", ")}. Their tools are refused until the budget is raised.</p>` : "") +
    (waiting.length ? `<p class="warnline amber">Waiting on you: ${waiting.map(w => `<a href="${esc(w.href)}">${esc(w.name)}</a>`).join(", ")}.</p>` : ""));
  const n = S.notifications || {unread: 0, items: []};
  const badge = $("#badge");
  badge.hidden = !n.unread; badge.textContent = n.unread > 99 ? "99+" : n.unread;
  document.title = (n.unread ? `(${n.unread}) ` : "") + "Pilot";
  const shown = new Map(n.items.map(i => [i.id, i]));
  put($("#notes"), n.items.length ? n.items.map(i => `<div class="note ${i.read ? "" : "unread"}" data-note="${esc(i.id)}" data-href="${esc(i.href || `#/w/${i.worker}`)}">
      <span>${NOTE_ICON[i.kind] || "•"}</span><div>${esc(i.text)}${i.times > 1 ? ` <span class="times">×${i.times}</span>` : ""}<div class="when">${esc(when(i.at))}</div>${settledLine(i, shown)}</div></div>`).join("")
    : `<div class="empty">Nothing yet. Notices pile up here while the dashboard runs: a pilot or worker waiting on you, a turn that ended, a budget reached, 30 quiet minutes, an error.</div>`);
  $("#desk").textContent = "Notification" in window && Notification.permission === "granted" ? "desktop on" : "desktop";
}

// Why a notice was acked without the controller: "superseded by" the later notice (a link to it while the bell
// still shows it), or the reason, answered or merged, or Haiku's one line.
function settledLine(i, shown) {
  if (!i.settled) return "";
  const by = i.settledBy && shown.get(i.settledBy);
  const line = by && i.settled === `superseded by ${i.settledBy}`
    ? `superseded by <a href="#" data-jump="${esc(i.settledBy)}">${esc(by.text.length > 60 ? by.text.slice(0, 60) + "…" : by.text)}</a>`
    : esc(i.settled);
  return `<div class="settled">↳ ${line}${i.settledByJudge ? " <span class=\"sub\">(Haiku)</span>" : ""}</div>`;
}

function desktop() {
  const items = S.notifications?.items || [];
  const newest = items[0]?.id || "", seen = local("pilot.noteSeen");
  if (seen === null) { local("pilot.noteSeen", newest); return; }
  if ("Notification" in window && Notification.permission === "granted")
    for (const i of items.filter(i => !i.read && i.id > seen).reverse()) {
      const note = new Notification(`Pilot: ${i.worker}`, {body: i.text, tag: i.id});
      note.onclick = () => { window.focus(); location.hash = i.href || `#/w/${i.worker}`; };
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

// ------------------------------------------------------------------ pilots

// A pilot's state is Claude Code's own: busy, idle, or waiting on you (for a question or a permission prompt).
// An idle pilot with legs in the air is following them: it wakes when one of them ends its turn.
function pilotChip(ps, flying = []) {
  if (!ps) return `<span class="chip other">pilot not running</span>`;
  if (ps.status === "waiting") return `<span class="chip blocked" title="${esc(ps.waitingFor || "")}">waiting on ${S.autopilot?.enabled ? "the autopilot" : "you"}${ps.since ? ` · ${esc(ago(ps.since).replace(" ago", ""))}` : ""}</span>`;
  if (ps.busy) return `<span class="chip working">● working</span>`;
  return `<span class="chip other">${flying.length ? `idle, following ${esc(flying.join(", "))}` : "idle"}</span>`;
}

// The first line of a reply, without its Markdown: enough to say what it was about.
const gist = s => (String(s || "").split("\n").map(l => l.replace(/[*`]/g, "").replace(/^[\s#>-]+/, "").trim()).find(Boolean) || "");
const nowText = n => n.kind === "tool" ? `<b>${esc(n.tool)}</b> ${esc(n.summary || "")}` : esc(gist(n.text));

// What the pilot is doing, or the question it is waiting on you for, with where to answer it.
function pilotNow(ps) {
  if (!ps) return "";
  const where = `answer it in the pilot's terminal${ps.remote ? ` or <a href="${esc(ps.remote)}" target="_blank" rel="noopener">remote ↗</a>` : ""}`;
  if (ps.status === "waiting" && ps.ask?.questions?.length)
    return `<div class="ask"><div class="cardhead"><b>The pilot is asking you</b><span class="sub">since ${esc(when(ps.since || ps.ask.at))} · ${where}</span></div>
      ${ps.ask.questions.map(q => `<div class="q"><div>${q.header ? `<span class="hdr">${esc(q.header)}</span>` : ""}${esc(q.question)}${q.multiSelect ? ` <span class="sub">(any of)</span>` : ""}</div>
        <ul>${(q.options || []).map(o => `<li><b>${esc(o.label)}</b>${o.description ? ` <span class="sub">${esc(o.description)}</span>` : ""}</li>`).join("")}</ul></div>`).join("")}</div>`;
  if (ps.status === "waiting")
    return `<p class="warnline amber">The pilot is waiting on you (${esc(ps.waitingFor || "input")}) since ${esc(when(ps.since))}: ${where}.
      Its question shows here when the question hook or its transcript has it; this time neither does, so it may be a permission prompt.</p>`;
  return ps.now ? `<div class="pilotnow sub">${ps.busy ? "now" : "last"}: ${nowText(ps.now)}</div>` : "";
}

// ------------------------------------------------------------------ flight plans

// What a leg leaves in its repo: commits past its base, pushed or not (to a remote branch of any name), and
// uncommitted files in its worktree. `git` is pilot.py's collateral(); null until its first read lands.
// A pull request as a link: its number, state and checks, in the colour of where it stands.
function prChip(p, withRepo) {
  const cls = p.state === "MERGED" ? "ok" : p.state === "CLOSED" || p.ci === "failing" ? "bad" : p.ci === "passing" ? "ok" : "sub";
  const what = [p.state === "OPEN" ? "open" : p.state?.toLowerCase(), p.state !== "MERGED" && p.state !== "CLOSED" && p.ci !== "none" ? `checks ${p.ci}` : "",
                p.review === "APPROVED" ? "approved" : p.review === "CHANGES_REQUESTED" ? "changes requested" : ""].filter(Boolean).join(", ");
  return `<a class="prchip ${cls}" href="${esc(p.url)}" target="_blank" rel="noopener" title="${esc(p.title || "")}">${withRepo && p.repo ? esc(p.repo) + " " : ""}#${esc(p.number)} ${esc(what)} ↗</a>`;
}

// Where a leg's PR stands in its review: on approach, go-around, holding, to the tower, cleared to land, at the gate.
function approachChip(ap) {
  if (!ap) return "";
  const cls = ap.stage === "cleared" || ap.stage === "gate" ? "ok" : ap.stage === "goaround" || ap.stage === "tower" ? "bad" : "sub";
  return `<span class="prchip ${cls}" title="${esc(ap.why)}">✈ ${esc(ap.label)}</span>`;
}

function gitLine(g, ap) {
  if (!g) return "";
  const tip = `${g.branch}${g.head ? ` at ${g.head}` : ""}, from ${g.base || "?"}. Remote branches as of the repo's last fetch or push.`;
  if (g.pending) return `<div class="gitline sub" title="${esc(tip)}">⎇ branch not made yet</div>`;
  if (g.gone) return `<div class="gitline sub" title="${esc(tip)}">⎇ branch deleted</div>`;
  if (g.error) return `<div class="gitline sub" title="${esc(g.error)}">⎇ git could not read it</div>`;
  const bits = [g.ahead ? `${g.ahead} commit${g.ahead === 1 ? "" : "s"}` : `<span class="sub">no commits</span>`];
  if (g.ahead) bits.push(g.unpushed ? `<span class="bad">${g.unpushed} not pushed</span>`
    : `<span class="ok">pushed${g.remotes?.length ? ` · ${esc(g.remotes[0])}` : ""}</span>`);
  if (g.dirty) bits.push(`<span class="bad">${g.dirty} uncommitted</span>`);
  for (const p of g.prs || []) bits.push(prChip(p) + (ap && ap.pr === p.number ? " " + approachChip(ap) : ""));
  return `<div class="gitline" title="${esc(tip)}">⎇ ${bits.join(" · ")}</div>`;
}
// The flight's: which legs hold commits on no remote, or uncommitted files.
function flightGit(p, s) {
  const gs = p.legs.map(l => [l.leg, legState(s, l.leg)]).map(([n, {w, h}]) => [n, (w || h)?.git]).filter(([, g]) => g && !g.gone && !g.error);
  if (!gs.some(([, g]) => g.ahead || g.dirty)) return "";
  const un = gs.filter(([, g]) => g.unpushed), dirty = gs.filter(([, g]) => g.dirty);
  if (!un.length && !dirty.length) return ` · <span class="gitsum ok">⎇ all pushed</span>`;
  return ` · <span class="gitsum bad">⎇ ${[un.length && `not pushed: ${un.map(([n, g]) => `${esc(n)} (${g.unpushed})`).join(", ")}`,
    dirty.length && `uncommitted: ${dirty.map(([n, g]) => `${esc(n)} (${g.dirty})`).join(", ")}`].filter(Boolean).join("; ")}</span>`;
}

// A leg's state: its worker's if one flies it now, else how its removed worker landed, else planned.
function legState(s, name) {
  const w = s.workers.find(x => x.name === name);
  if (w) return {w, st: stateOf(w)};
  const h = s.history.find(x => x.name === name);
  if (h) return {h, st: {...stateOf(h), label: stateOf(h).label + " · removed"}};
  return {st: {cls: "other", label: "planned", stroke: "var(--axis)", tint: "var(--surface)", planned: true}};
}

// An alternate leg, filed in the plan's Alt of column, is taken once a worker flies it, or a removed one landed:
// pilot.py's alt_taken. Removing its worker before it lands undoes the diversion.
function altTaken(s, l) { const {w, h, st} = legState(s, l.leg); return !!w || (!!h && st.cls === "done"); }

// The route as flown, pilot.py's flown(): each primary leg, or in its place the alternates taken for it (any one
// landed lands it). An alternate not taken never keeps a flight from landing and is not counted as one of its legs.
function flownLegs(p, s) {
  const taken = new Map();
  for (const l of p.legs) if (l.altOf && altTaken(s, l)) taken.set(l.altOf, [...(taken.get(l.altOf) || []), l]);
  return p.legs.filter(l => !l.altOf).map(l => {
    const a = taken.get(l.leg);
    return a ? a.find(x => legState(s, x.leg).st.cls === "done") || a[a.length - 1] : l;
  });
}

// Each leg's column, pilot.py's route_depths(), which the server sends as p.depths: its longest path back through
// After. An alternate takes its primary's column, and an After naming one counts as naming its primary.
function routeDepths(legs) {
  const prim = new Map(legs.filter(l => l.altOf).map(l => [l.leg, l.altOf]));
  const by = new Map(legs.filter(l => !l.altOf).map(l => [l.leg, l])), out = {};
  const depth = (n, path = new Set()) => {
    if (n in out) return out[n];
    if (path.has(n)) return 0;
    const deps = by.get(n).after.map(a => prim.get(a) || a).filter(d => by.has(d));
    return out[n] = deps.length ? 1 + Math.max(...deps.map(d => depth(d, new Set([...path, n])))) : 0;
  };
  for (const n of by.keys()) depth(n);
  for (const [a, of] of prim) out[a] = out[of] || 0;
  return out;
}

// What "your move" is, once the pilot's turn has ended with nothing in the air: the items waiting on the
// controller; for a landed flight, its open pull requests and what each needs; else the last line of the
// pilot's last message, which is where a pilot says what it is waiting for.
// With the autopilot on, what waits on the controller is the autopilot's to decide, a merge excepted (it never merges).
function yourMoveOf(p, s, ps) {
  const auto = S.autopilot?.enabled;
  if (p.waiting && !unanswered(p).length) return `answers sent; the pilot's move`;
  if (p.waiting) {
    const items = unanswered(p), merges = items.filter(i => /^\**\s*merge/i.test(i)).length, n = items.length - (auto ? merges : 0);
    if (auto && !n) return `your move: ${merges === 1 ? "a merge" : `${merges} merges`} below`;
    return `${auto ? "autopilot's move" : "your move"}: answer ${n} item${n === 1 ? "" : "s"} below${auto && merges ? `, and ${merges === 1 ? "a merge" : `${merges} merges`} for you` : ""}`;
  }
  const legs = flownLegs(p, s).map(l => legState(s, l.leg));
  const prs = [...new Map(legs.flatMap(({w, h}) => (w || h)?.git?.prs || []).map(x => [x.url, x])).values()]
    .filter(x => x.state !== "MERGED" && x.state !== "CLOSED");
  const link = x => `<a href="${esc(x.url)}" target="_blank" rel="noopener">#${esc(x.number)}</a>`;
  const landed = legs.length && legs.every(({st}) => st.cls === "done");
  // A team-reviewed PR goes to its reviewers and is merged once approved; a self-reviewed one (the pilot and
  // its co-pilot are its review, as on a repo of your own) is merged once its checks pass.
  const green = x => x.ci === "passing" ? " (checks green)" : "";
  // While the flight's own review of a PR is under way (a round due, a go-around, CI running), the move is the pilot's.
  const aps = new Map(legs.map(({w, h}) => (w || h)?.approach).filter(Boolean).map(a => [a.pr, a]));
  const step = x => ["approach", "goaround", "holding"].includes(aps.get(x.number)?.stage) ? `${link(x)}: ${esc(aps.get(x.number).why)} (the pilot's)`
    : aps.get(x.number)?.stage === "tower" ? `${link(x)}: its review rounds are spent; decide the bugs left`
    : x.ci === "failing" ? `${link(x)}: checks failing` : x.ci === "running" ? `${link(x)}: checks running`
    : x.reviewer === "self" ? `${x.state === "DRAFT" ? "mark ready and merge" : "merge"} ${link(x)}${green(x)}`
    : x.state === "DRAFT" ? `mark ${link(x)} ready for review${green(x)}`
    : x.review === "APPROVED" ? `merge ${link(x)}` : x.review === "CHANGES_REQUESTED" ? `${link(x)}: changes requested`
    : `${link(x)} waits on its reviewers`;
  if (landed && prs.length) return "landed · your move: " + prs.map(step).join(", ");
  // Every leg landed and nothing left open: the flight is done, whatever the pilot's last words were. Only once
  // every leg's git has been read, though: until then its PRs are unknown, not absent.
  if (landed && legs.every(({w, h}) => (w || h)?.git)) return "";
  // No open PR known (none, or not looked up yet): the pilot's own words say what it is waiting for.
  const last = String(ps?.said?.text || "").split("\n").map(l => l.replace(/[*`]/g, "").replace(/^[\s#>-]+/, "").trim()).filter(Boolean).pop();
  return `${landed ? "landed · " : ""}${auto ? "autopilot's move" : "your move"}${last ? `: ${esc(last.length > 110 ? last.slice(0, 107) + "…" : last)}` : ""}`;
}

function planCard(p, s) {
  const legs = p.legs.map(l => {
    const {w, h, st} = legState(s, l.leg);
    const sp = w ? w.spend : h ? h.final?.spend : null;
    const link = w ? `#/w/${esc(w.name)}` : h ? `#/s/${esc(h.sessionId)}` : "";
    // An alternate says whose it is, and is no leg of the route until it is taken; once taken, why.
    const alt = l.altOf ? `<div class="sub">alternate for ${esc(l.altOf)}</div>` : "", out = l.altOf && !altTaken(s, l);
    const why = (w || h)?.divertedFrom ? `<div class="sub">diverted from ${esc((w || h).divertedFrom)}: ${esc((w || h).divertReason || "")}</div>` : "";
    return `<tr class="${link ? "click" : ""}${l.altOf ? " altleg" : ""}"${link ? ` data-href="${link}"` : ""}><td><b>${esc(l.leg)}</b>${alt}</td><td>${md(l.goal)}</td><td>${esc(l.after.join(", ") || "-")}</td>
      <td>${md(l.doneWhen)}</td><td>${out ? `<span class="chip other">alternate, not taken</span>` : `<span class="chip ${st.cls}">${esc(st.label)}</span>`}${why}${w && (w.state === "working" || waitsOnYou(w)) ? doing(w) : ""}${checkLine(w ? w.checklist : h?.final?.checklist)}${gitLine((w || h)?.git, (w || h)?.approach)}</td><td class="num">${sp ? esc(M(sp.weighted)) : ""}</td></tr>`;
  }).join("");
  const log = p.log.slice(-12).reverse().map(l => `<li>${esc(l)}</li>`).join("");
  const ps = p.pilotState, waiting = ps?.status === "waiting";
  const flying = p.legs.map(l => l.leg).filter(n => s.workers.some(w => w.name === n && w.state === "working"));
  const fold = (k, def) => `data-k="${esc(k)}"${def ? ` data-open="1"` : ""}${(opened.has(k) ? opened.get(k) : def) ? " open" : ""}`;
  // The pilot's turn has ended and nothing is in the air: whatever happens next is up to the controller.
  // A flight whose legs have all landed with nothing left open reads landed, whether or not its pilot still runs.
  const settled = !flying.length ? yourMoveOf(p, s, ps) : null;
  const move = p.live && ps?.status === "idle" ? settled : settled === "" ? "" : null;
  const yourMove = !!move, atGate = move === "";
  // The flight's state sits by its name: whose move it is, the pilot at work, or landed. A flight holding at the
  // gate says so first: ready once its pilot has a route and has ended its turn, else still being planned.
  const ready = holdReady(p);
  const status = p.hold ? `${holdChip(p, ready)}${ready ? "" : ` ${p.asleep ? `<span class="chip other">pilot asleep</span>` : pilotChip(ps)}`}`
    : atGate ? `<span class="chip done">✓ landed</span>`
    : yourMove ? `<span class="chip blocked move" title="${esc(ps.said?.text || "")}">${move}</span>` : p.asleep ? `<span class="chip other" title="Its background session has ended. The dashboard wakes it, as a copy, when one of its legs stops or a message comes for it.">pilot asleep${flying.length ? `, wakes when ${esc(flying.join(", "))} stops` : ""}</span>` : pilotChip(ps, flying);
  const where = `answer in the pilot's terminal${ps?.remote ? ` or <a href="${esc(ps.remote)}" target="_blank" rel="noopener">remote ↗</a>` : ""}`;
  // With more than one flight each starts folded, so the page reads as a list of flights, unless it waits on you;
  // alone, an active one starts open. A fold you set stays as you left it.
  const many = (s.plans || []).length > 1;
  return `<details class="card pad plan" id="plan-${esc(p.sessionId)}" data-order="${esc(p.sessionId)}" ${fold("plan:" + p.sessionId, !!p.waiting || (!many && planActive(p, s)))}>
    <summary class="cardhead"><span>${reorderHtml(p, s)}<b>${p.flight ? `Flight ${esc(p.flight)}` : "Flight plan"} · ${esc(p.title)}</b> ${connectsHtml(p)} <span class="sub">${esc(legTally(p, s))}${flightGit(p, s)}${p.times?.departed ? ` · ${timesLine(p.times)}` : ""}</span> ${status}</span>
      <span class="sub">${p.live ? `pilot ${esc(p.pilot || "")} · ` : ""}plan updated ${esc(ago(p.updated))}
      ${/^https:\/\/claude\.ai\//.test(p.tracking || "") ? `· <a href="${esc(p.tracking)}" target="_blank" rel="noopener" title="the flight's tracking page, as of the pilot's last publish">tracking ↗</a> ` : ""}· <a href="vscode://file/${esc(p.path.replace(/\\/g, "/"))}" title="${esc(p.path)}">edit</a> · <a href="#/s/${esc(p.sessionId)}">pilot session ›</a></span></summary>
    ${p.objective ? `<div class="objective">${md(p.objective)}</div>` : ""}
    ${p.hold ? clearanceCard(p, ready) : timesCard(p.times)}
    ${connectsCard(p)}
    ${(p.prs || []).length ? `<div class="prline"><span class="sub">pull requests the plan names:</span> ${p.prs.map(x => prChip(x, true)).join(" ")}</div>` : ""}
    ${p.waiting ? waitingHtml(p, ps) : ""}
    ${yourMove && !atGate && ps.said ? `<details class="lastsaid" ${fold("said:" + p.sessionId, true)}><summary>its turn ended ${esc(ago(Date.parse(ps.said.at) / 1000))} with nothing in the air; its last message</summary>${md(ps.said.text)}</details>` : pilotNow(ps)}
    ${p.notes ? `<details ${fold("notes:" + p.sessionId, waiting)}><summary>the pilot's notes: what it found and why the route is this shape</summary>${md(p.notes, true)}</details>` : ""}
    ${p.legs.length > 1 ? `<details ${fold("chart:" + p.sessionId, true)}><summary>the route as a chart: each leg after the legs it waits for</summary><div class="graph">${routeChart(p, s)}</div></details>` : ""}
    <div class="tablewrap"><table><thead><tr><th>Leg</th><th>Goal</th><th>After</th><th>Done when</th><th>Status</th><th class="num">Spend</th></tr></thead>
      <tbody>${legs || `<tr><td colspan="6" class="sub">No legs yet. The pilot adds them to the Route table.</td></tr>`}</tbody></table></div>
    ${log ? `<details ${fold("log:" + p.sessionId, true)}><summary>flight log: decisions, questions and answers, legs taking off and landing</summary><ul class="log">${log}</ul></details>` : ""}
    <div class="planfoot"><button class="arch" data-archive="${esc(p.sessionId)}" data-name="${esc(p.flight ? `Flight ${p.flight}` : "this flight")} · ${esc(p.title)}" title="Off the Overview and the Flights strips, onto History; nothing is deleted, and History restores it">Archive flight…</button></div></details>`;
}

// ------------------------------------------------------------------ answering the pilot from the dashboard

// The plan's waiting items, as the pilot skill asks for them:
//   - **<question>** <context>. Options: A | B. Recommend: A.
// Each gets buttons (its options, or accepting the recommendation) and a box for a reply of your own. Send
// relays the answers to the pilot through the intercom. Choices are kept, across re-renders, until sent.
const answers = new Map(), sentNote = new Map();

function waitingItems(src) {
  const intro = [], items = [];
  for (const l of String(src || "").split("\n")) {
    if (/^([-*]|\d+[.)])\s/.test(l)) items.push(l.replace(/^([-*]|\d+[.)])\s+/, ""));
    else if (items.length && l.trim()) items[items.length - 1] += " " + l.trim();
    else if (!items.length) intro.push(l);
  }
  return {intro: intro.join("\n").trim(), items: items.map(text => {
    const title = ((/^\*\*(.+?)\*\*/.exec(text) || [])[1] || text.split(/[.?]\s/)[0]).trim();
    const opts = /Options:\s*(.+?)(?:\.\s+Recommend|\.\s*$|$)/.exec(text), rec = /Recommend(?:ation)?:\s*(.+?)\s*$/.exec(text);
    return {text, title, options: opts ? opts[1].split(/\s+\|\s+/).map(s => s.trim()).filter(Boolean) : [],
            rec: rec ? rec[1].replace(/\.$/, "") : "", recShort: rec ? rec[1].split(/[.,;(]/)[0].trim() : ""};
  })};
}

// "PR #n" in a plan's text, linked to the flight's repo on GitHub.
const prLinks = (html, url) => url ? html.replace(/\bPR #(\d+)\b/g, (m, n) => `<a href="${esc(url)}/pull/${n}" target="_blank" rel="noopener">${m}</a>`) : html;

// The plan's waiting items not yet answered from here.
const unanswered = p => waitingItems(p.waiting).items.filter(it => !(p.answered || {})[it.title]);

function waitingHtml(p, ps) {
  const {intro, items} = waitingItems(p.waiting), sid = p.sessionId;
  const where = ps?.remote ? ` or <a href="${esc(ps.remote)}" target="_blank" rel="noopener">remote ↗</a>` : "";
  let n = 0;
  const rows = items.map((it, i) => {
    const sent = (p.answered || {})[it.title];
    if (sent) return `<li class="answered">${md(it.text, true)}<div class="sub">✓ answered ${esc(clock(sent * 1000))}; with the pilot, which takes it out of its plan at its next step</div></li>`;
    const a = answers.get(`${sid}|${it.title}`);
    // A merge question gets the controller's own merge button: the dashboard runs it, as a pilot may not.
    const mpr = (/\bmerge PR #(\d+)/i.exec(it.title) || [])[1];
    const mergeBtn = mpr && p.repoUrl ? `<button class="primary" data-merge="${esc(sid)}" data-pr="${esc(mpr)}">Merge #${esc(mpr)} (rebase)</button>
      <a href="${esc(p.repoUrl)}/pull/${esc(mpr)}" target="_blank" rel="noopener">PR #${esc(mpr)} ↗</a>` : "";
    if (a) n++;
    const btn = (kind, value, label, rec) => `<button class="choice${a?.kind === kind && a.value === value ? " on" : ""}${rec ? " rec" : ""}"
      data-answer="${esc(sid)}" data-item="${i}" data-kind="${kind}" data-value="${esc(value)}" title="${esc(rec ? `recommended: ${it.rec}` : value)}">${esc(label)}</button>`;
    const plain = s => s.replace(/[`*]/g, ""), short = plain(it.recShort);
    const choices = it.options.length ? it.options.map(o => btn("option", o, plain(o), o === it.recShort || it.rec.startsWith(o))).join("")
      : it.rec ? btn("accept", it.rec, `accept: ${short.length > 40 ? short.slice(0, 39) + "…" : short}`, true) : "";
    return `<li>${prLinks(md(it.text, true), p.repoUrl)}${mergeBtn ? `<div class="choices">${mergeBtn}</div>` : ""}<div class="choices">${choices}<input class="reply" data-answer="${esc(sid)}" data-item="${i}"
      placeholder="${choices ? "or your own answer" : "your answer"}" value="${esc(a?.kind === "text" ? a.value : "")}"></div></li>`;
  }).join("");
  const can = p.live && p.pilot;
  const left = unanswered(p).length;
  return `<div class="ask${left ? "" : " done"}"><div class="cardhead"><b>${left ? "Waiting on you" : "Answered, with the pilot"}</b><span class="sub">from the plan · answer here, in the pilot's terminal${where}</span></div>
    ${intro ? md(intro, true) : ""}<ol class="items">${rows}</ol>
    <div class="sendrow"><button class="primary" data-sendanswers="${esc(sid)}"${n && can ? "" : " disabled"}>Send ${n || ""} answer${n === 1 ? "" : "s"} to the pilot</button>
      <span class="sub">${esc(sentNote.get(sid) || (can ? "through the intercom; it reaches the pilot at its next step" : "the pilot is not running"))}</span></div></div>`;
}

async function sendAnswers(sid) {
  const p = (S.plans || []).find(x => x.sessionId === sid);
  if (!p?.pilot) return;
  const picked = waitingItems(p.waiting).items.map(it => [it, answers.get(`${sid}|${it.title}`)]).filter(([, a]) => a);
  if (!picked.length) return;
  sentNote.set(sid, "sending…"); render();
  // What the controller submits here is done here: a Merge answer to a merge item is the merge itself, run by
  // the dashboard, since a pilot told "Merge" is refused it (another session's message carries no authority).
  const merged = new Map();
  for (const [it, a] of picked) {
    const n = (/\bmerge PR #(\d+)/i.exec(it.title) || [])[1], said = a.kind === "accept" ? it.rec : a.value;
    if (!n || !/^\s*merge(d)?\b/i.test(said || "")) continue;
    try { const j = await post("/api/merge", {plan: sid, pr: Number(n)}); merged.set(it.title, `merged by the dashboard (rebase, guarded on ${j.head})`); }
    catch (e) { sentNote.set(sid, `not sent: merging PR #${n} failed: ${e.message}`); render(); return; }
  }
  const text = `The controller's answers to items in your plan's "Waiting on the controller" section, sent from the dashboard:\n`
    + picked.map(([it, a]) => `- ${it.title}: ${merged.get(it.title) || (a.kind === "accept" ? `accepted your recommendation (${it.rec})` : a.value)}`).join("\n")
    + `\nTake each answered item out of the section, log the answer, and carry on.`;
  try {
    const j = await post("/api/intercom", {to: [p.pilot], text, answered: {plan: sid, titles: picked.map(([it]) => it.title)}});
    for (const [it] of picked) answers.delete(`${sid}|${it.title}`);
    sentNote.set(sid, `sent ${picked.length}: ${j.result}`);
  } catch (e) { sentNote.set(sid, `not sent: ${e.message}`); }
  render();
}

// A plan is open by default while it is in the air: its pilot runs, or a worker flies one of its legs.
const planActive = (p, s) => p.live || p.legs.some(l => s.workers.some(w => w.name === l.leg && w.pid));

// "3 legs: 1 working, 2 planned", for a plan folded to one line. The legs are the route as flown; an alternate
// not taken is told apart after them.
function legTally(p, s) {
  const route = flownLegs(p, s);
  if (!route.length) return "no legs yet";
  const n = {};
  for (const l of route) {
    const {st} = legState(s, l.leg);
    const word = st.planned ? "planned" : {working: "working", blocked: "waiting on you", done: "done", failed: "failed"}[st.cls] || "ended";
    n[word] = (n[word] || 0) + 1;
  }
  const alts = p.legs.filter(l => l.altOf && !altTaken(s, l)).length;
  return `${route.length} leg${route.length > 1 ? "s" : ""}: ` + Object.entries(n).map(([k, v]) => `${v} ${k}`).join(", ")
    + (alts ? ` · ${alts} alternate${alts > 1 ? "s" : ""}` : "");
}

// ------------------------------------------------------------------ the route chart, and the flights

// One plan's legs, chained by After, drawn inside its plan. A planned leg no worker flies yet is dashed, and so is
// an alternate, in its primary's column.
function routeChart(p, s) {
  const items = p.legs.map(l => { const {w, st} = legState(s, l.leg); return {name: l.leg, after: l.after, altOf: l.altOf, w, st, started: w?.started || Infinity}; });
  if (!items.length) return "";
  const byName = new Map(items.map(x => [x.name, x])), depths = p.depths || routeDepths(p.legs);
  const depthOf = new Map(items.map(x => [x.name, depths[x.name] || 0]));
  const layers = [];
  for (const x of [...items].sort((a, b) => a.started - b.started)) (layers[depthOf.get(x.name)] ||= []).push(x);
  // Left to right, like the flight: a leg's depth in After is its column, and legs of one depth stack.
  const NW = 150, NH = 62, GX = 28, GY = 14, PAD = 8;
  const tallest = Math.max(...layers.map(l => (l || []).length));
  const width = layers.length * (NW + GX) - GX + 2 * PAD, height = tallest * (NH + GY) - GY + 2 * PAD;
  const pos = new Map();
  layers.forEach((l, i) => (l || []).forEach((x, j) => {
    const colH = l.length * (NH + GY) - GY;
    pos.set(x.name, {x: PAD + i * (NW + GX), y: PAD + (height - 2 * PAD - colH) / 2 + j * (NH + GY)});
  }));
  const edges = [];
  for (const x of items) for (const d of x.after.filter(d => byName.has(d))) {
    const a = pos.get(d), b = pos.get(x.name);
    const x1 = a.x + NW, y1 = a.y + NH / 2, x2 = b.x, y2 = b.y + NH / 2;
    edges.push(`<path class="edge" d="M${x1},${y1} C${x1 + GX / 2},${y1} ${x2 - GX / 2},${y2} ${x2},${y2}"/>`);
  }
  const clip = (t, n) => t.length > n ? t.slice(0, n - 1) + "…" : t;
  const nodes = items.map(({w, st, name, altOf}) => {
    const {x, y} = pos.get(name), f = w && w.budget ? Math.min(1, w.spend.weighted / w.budget) : 0;
    const dash = altOf ? ";stroke-dasharray:2 3" : st.planned ? ";stroke-dasharray:5 4" : "";
    const label = altOf && !altTaken(s, {leg: name}) ? `alt of ${altOf}` : st.label;
    return `<g class="node"${w ? ` data-href="#/w/${esc(w.name)}"` : ""}><title>${esc(w?.detail || (altOf ? `alternate for ${altOf}, taken only on a diversion` : st.planned ? "planned, not started" : ""))}</title>
      <rect class="box" x="${x}" y="${y}" width="${NW}" height="${NH}" rx="8" style="fill:${st.tint};stroke:${st.stroke};stroke-width:1.5${dash}"/>
      <text x="${x + 12}" y="${y + 20}" font-weight="700">${esc(clip(name, 18))}</text>
      <text class="sm" x="${x + 12}" y="${y + 37}">${esc(label)}${w ? ` · ${esc(M(w.spend.weighted))}` : ""}</text>
      ${w ? `<rect x="${x + 12}" y="${y + 47}" width="${NW - 24}" height="4" rx="2" style="fill:var(--track)"/>
      <rect x="${x + 12}" y="${y + 47}" width="${Math.max(2, (NW - 24) * f)}" height="4" rx="2" style="fill:${f >= 1 ? "var(--crit)" : f >= .75 ? "var(--warn)" : "var(--fill)"}"/>` : ""}</g>`;
  });
  return `<svg width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="the route">${edges.join("")}${nodes.join("")}</svg>`;
}

// An airplane pointing right, about 30 by 24, centred on its middle.
const PLANE = "M15,0 L5,-2.2 L-1,-12 L-5,-12 L-2,-2.2 L-9,-2.2 L-12.5,-7 L-15,-7 L-13,0 L-15,7 L-12.5,7 L-9,2.2 L-2,2.2 L-5,12 L-1,12 L5,2.2 Z";

// A flight's times, pilot.py's flight_times(): when its first leg took off, its ETA projected from active time
// (or, once every leg has landed, its arrival), and its delay against its schedule, the first ETA it had.
// Early is a negative delay. A time on another day than today says which day.
const tclock = ts => new Date(ts * 1000).toDateString() === new Date().toDateString() ? hhmm(ts) : dayhhmm(ts);
function delayHtml(d) {
  if (d == null) return "";
  const m = Math.round(Math.abs(d) / 60), span = m < 90 ? `${m}m` : `${Math.floor(m / 60)}h${String(m % 60).padStart(2, "0")}`;
  return m < 1 ? `<span class="early">on time</span>` : `<span class="${d > 0 ? "late" : "early"}">${d > 0 ? "+" : "−"}${span}</span>`;
}
function timesLine(t) {
  if (!t?.departed) return "";
  const end = t.arrived ? `ARR ${tclock(t.arrived)}` : t.eta ? `ETA ${tclock(t.eta)}` : "no ETA yet";
  return `<span class="ftimes" title="${esc(t.noEta || t.basis || "")}">DEP ${tclock(t.departed)} · ${end}${t.delay != null ? ` · ${delayHtml(t.delay)}` : ""}</span>`;
}
function timesCard(t) {
  if (!t?.departed) return "";
  const late = t.delay == null ? "" : ` (${delayHtml(t.delay)}${Math.abs(t.delay) >= 60 ? (t.delay > 0 ? " late" : " early") : ""})`;
  return `<div class="times ftimes"><span><b>departed</b>${esc(when(t.departed))}</span>
    ${t.arrived ? `<span><b>arrived</b>${esc(when(t.arrived))}${late}</span>` : t.eta ? `<span><b>ETA</b>${esc(when(t.eta))}${late}</span>` : `<span><b>ETA</b>${esc(t.noEta || "no ETA yet")}</span>`}
    <span><b>scheduled</b>${t.scheduled ? esc(when(t.scheduled)) : "not yet: the first ETA sets it"}</span>
    ${t.basis ? `<span class="basis sub">${t.arrived ? "" : "ETA from "}${esc(t.basis)}</span>` : ""}</div>`;
}

// A flight launched to plan only holds at the gate (its plan's Hold: line) until the controller clears it: its
// pilot plans, then ends its turn. Ready once it has a route and its pilot has stopped planning; it has no
// departure, so no ETA either. Cleared for takeoff lifts the hold and tells the pilot, or wakes it.
const holdReady = p => !!p.hold && p.legs.length > 0 && !p.pilotState?.busy && p.pilotState?.status !== "waiting";
const holdChip = (p, ready) => `<span class="chip hold" title="Holding at the gate: no leg takes off until you clear it for takeoff">${ready ? "ready · awaiting clearance" : "planning · holding for clearance"}</span>`;
function clearanceCard(p, ready) {
  const name = `${p.flight ? `Flight ${p.flight}` : "this flight"} · ${p.title}`;
  return `<div class="clearance${ready ? " ready" : ""}"><div><b>Holding at the gate</b> <span class="sub">${ready
      ? `the route is planned and ${p.live || p.asleep ? "the pilot has ended its turn" : "the pilot is not running"}: ${p.legs.filter(l => !l.altOf).length} leg${p.legs.filter(l => !l.altOf).length === 1 ? "" : "s"} wait for your clearance`
      : "the pilot is still planning; no leg takes off until you clear it"}</span>
    <div class="times ftimes"><span><b>departure</b>none yet</span><span><b>ETA</b>${esc(p.times?.noEta || "awaiting clearance: no departure yet")}</span></div></div>
    <button class="primary" data-clear="${esc(p.sessionId)}" data-name="${esc(name)}" title="Lift the hold, log it, and tell the pilot to take off (waking it if its session has ended)">Cleared for takeoff</button></div>`;
}

// The flights a flight connects from, pilot.py's connection(): its legs take off only once each has landed, so
// one not landed yet reads waiting, and a number no flight has reads as the error it is. Once it has landed it is
// plain. Each opens its own flight's plan.
function connectChip(c) {
  const cls = c.landed ? "landed" : c.known ? "waiting" : "unknown";
  const tip = c.known ? `connects from ${c.flight}${c.title ? ` · ${c.title}` : ""}: ${c.state}${c.open?.length ? `; still open: ${c.open.join("; ")}` : ""}`
    : `connects from ${c.flight}, but ${c.state}: a typo? pilot.py connect --remove ${c.flight}`;
  return `<span class="connect ${cls}"${c.sessionId ? ` data-plan="${esc(c.sessionId)}"` : ""} title="${esc(tip)}">⇠ ${esc(c.flight)}${c.landed ? "" : c.known ? " · waiting" : " · no such flight"}</span>`;
}
const connectsHtml = p => (p.connections || []).map(connectChip).join(" ");
function connectsCard(p) {
  const cs = p.connections || [];
  if (!cs.length) return "";
  const held = cs.filter(c => !c.landed);
  return `<div class="connects"><span class="sub">connects from</span> ${cs.map(c => `${connectChip(c)} <span class="sub">${esc(c.title ? `${c.title}: ` : "")}${esc(c.state)}</span>`).join(" · ")}
    ${held.length ? `<span class="sub">— its legs take off once ${held.map(c => esc(c.flight)).join(", ")} ${held.length > 1 ? "have" : "has"} landed</span>` : ""}</div>`;
}

// Where a flight is: its route from the gate, left to right, a leg's depth in After its column (pilot.py's
// route_depths). Legs of one depth fly side by side as lanes: the route forks where several legs wait on one, and
// joins at a leg that waits on several; legs that end it join at its destination. An alternate is drawn dashed
// beside its primary, in its column, and counts for nothing until it is taken. Each leg in the air (working,
// waiting on you, failed or stopped) has its plane, as far along its track as its checklist, amber while it waits
// on you; an alternate only once taken, and a primary no longer once diverted. Before the first takeoff one plane
// stands at the gate; between legs it stands on the ground; when every leg of the route as flown is done it has landed.
function flightStrip(p, s) {
  const depth = p.depths || routeDepths(p.legs);
  const legs = p.legs.map(l => ({...l, name: l.leg, d: depth[l.leg] || 0, ...legState(s, l.leg)}));
  const by = new Map(legs.map(l => [l.name, l]));
  const route = flownLegs(p, s).map(l => by.get(l.leg));
  const onRoute = new Set(route.map(l => l.name));
  const primary = l => l && !l.altOf;
  const cols = 1 + Math.max(0, ...legs.filter(primary).map(l => l.d));
  // A column: its legs in the plan's order, each primary followed by its alternates.
  const col = Array.from({length: cols}, () => []);
  for (const l of legs.filter(primary)) col[l.d].push(l, ...legs.filter(a => a.altOf === l.name));
  for (const a of legs.filter(a => a.altOf && !primary(by.get(a.altOf)))) col[Math.min(a.d, cols - 1)].push(a);
  // The legs that end the route: no other leg waits on them. Several join at a destination past them.
  const waitedOn = new Set(legs.filter(primary).flatMap(l => l.after.map(n => by.get(n)?.altOf || n)));
  const ends = legs.filter(l => primary(l) && !waitedOn.has(l.name));
  const join = ends.length > 1;
  const LANE = 44, rows = Math.max(1, ...col.map(c => c.length));
  const W = 1000, H = 86 + (rows - 1) * LANE, Y = 50 + (rows - 1) * LANE / 2, X0 = 34, XN = W - 74;   // past XN: where it parks
  const seg = (XN - X0) / (cols + (join ? 1 : 0)), xAt = d => X0 + (d + 1) * seg;
  const pos = new Map(), gate = {x: X0, y: Y}, dest = join ? {x: xAt(cols), y: Y} : null;
  col.forEach((c, d) => c.forEach((l, i) => pos.set(l.name, {x: xAt(d), y: Y + (i - (c.length - 1) / 2) * LANE})));
  // A leg's track comes from each leg it waits for, or from the gate; its plane flies the one from the deepest. A
  // leg waited on that was diverted is left from its alternate, which flew in its place.
  const standIn = n => route.find(r => r.altOf === n)?.name || n;
  const from = l => { const a = [...new Set(l.after.map(standIn))].filter(n => pos.has(n)); return a.length ? a : [null]; };
  const at = n => n === null ? gate : pos.get(n);
  const main = l => from(l).reduce((m, n) => (n !== null && (m === null || by.get(n).d > by.get(m).d)) ? n : m);
  const curve = (a, b) => {
    const dx = (b.x - a.x) / 2, P = [a, {x: a.x + dx, y: a.y}, {x: b.x - dx, y: b.y}, b];
    return {d: `M${a.x.toFixed(1)},${a.y.toFixed(1)} C${P[1].x.toFixed(1)},${P[1].y.toFixed(1)} ${P[2].x.toFixed(1)},${P[2].y.toFixed(1)} ${b.x.toFixed(1)},${b.y.toFixed(1)}`,
            at: t => { const u = 1 - t, k = [u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t];
                       return {x: k.reduce((v, c, i) => v + c * P[i].x, 0), y: k.reduce((v, c, i) => v + c * P[i].y, 0)}; }};
  };
  const progress = l => { const it = l.w?.checklist || []; return it.length ? (it.length - openItems(it).length) / it.length : .5; };
  const colorOf = l => l.st.cls === "failed" ? "var(--crit)" : l.st.cls === "blocked" ? "var(--warn)" : l.st.cls === "working" ? "var(--fill)" : "var(--muted)";
  // In the air: a leg of the route as flown that has a worker and has not landed.
  const air = route.filter(l => !l.st.planned && l.st.cls !== "done");
  const planes = air.map(l => {
    const t = Math.min(.9, Math.max(.1, progress(l))), pt = curve(at(main(l)), pos.get(l.name)).at(t);
    const up = l.st.cls === "working" || l.st.cls === "blocked";    // its turn ended short of DONE: it stands there
    return {x: pt.x, y: pt.y - (up ? 15 : 0), up, color: colorOf(l), name: l.name, t};
  });
  const landed = route.length > 0 && route.every(l => l.st.cls === "done");
  let status;
  if (!legs.length) { planes.push({x: X0, y: Y, color: "var(--muted)"}); status = "no route yet"; }
  else if (landed) { planes.push({x: XN + 40, y: Y, color: "var(--good)"}); status = "landed"; }
  else if (!air.length) {
    // On the ground: at the gate before the first takeoff, else where the next legs leave from.
    // A leg waited on is done once what flies in its place (itself, or its taken alternate) is.
    const done = n => { const k = by.get(n)?.altOf || n, r = route.find(r => r.name === k || r.altOf === k); return !r || r.st.cls === "done"; };
    const next = route.filter(l => l.st.planned && l.after.every(done));
    const first = next[0], fromAt = first ? at(main(first)) : gate;
    planes.push({x: fromAt.x, y: fromAt.y, color: "var(--muted)"});
    const names = next.map(l => esc(l.name)).join(", ");
    status = route.some(l => !l.st.planned) ? `on the ground${names ? ` · next: ${names}` : ""}` : `at the gate${names ? ` · first: ${names}` : ""}`;
    if (p.hold) status = `holding at the gate${names ? ` · first: ${names}` : ""} · no departure until cleared`;
  } else {
    const named = (cls, f) => air.filter(l => l.st.cls === cls).map(f);
    const prog = l => { const it = l.w?.checklist || []; return it.length ? ` (${it.length - openItems(it).length} of ${it.length})` : ""; };
    const flying = named("working", l => esc(l.name) + prog(l)), hold = named("blocked", l => `${esc(l.name)}: ${esc(l.st.label)}`);
    const failed = named("failed", l => esc(l.name)), stopped = named("other", l => `${esc(l.name)}, ${esc(l.st.label)}`);
    status = [flying.length && `${flying.length} flying: ${flying.join(", ")}`, hold.length && `holding · ${hold.join("; ")}`,
              failed.length && `failed: ${failed.join(", ")}`, stopped.length && `stopped: ${stopped.join("; ")}`].filter(Boolean).join(" · ");
  }
  const tracks = [], stops = [];
  const planeOf = new Map(planes.filter(pl => pl.name).map(pl => [pl.name, pl]));
  for (const l of legs) {
    const b = pos.get(l.name), alt = l.altOf ? " alt" : "", pl = planeOf.get(l.name);
    for (const n of from(l)) {
      const c = curve(at(n), b);
      if (l.st.cls === "done" && (onRoute.has(l.name) || !l.altOf)) tracks.push(`<path d="${c.d}" class="trk flown${alt}"/>`);
      else {
        tracks.push(`<path d="${c.d}" class="trk ahead${alt}"/>`);
        // Flown so far, up to its plane: the same curve, drawn to the plane's share of it.
        if (pl && n === main(l)) tracks.push(`<path d="${c.d}" pathLength="1" class="trk" style="stroke:${pl.color};stroke-dasharray:${pl.t.toFixed(3)} 2"/>`);
      }
    }
  }
  if (dest) for (const e of ends) {
    const stand = route.find(r => r.name === e.name || r.altOf === e.name) || e, c = curve(pos.get(stand.name), dest);
    tracks.push(`<path d="${c.d}" class="trk ${stand.st.cls === "done" ? "flown" : "ahead"}"/>`);
  }
  for (const l of legs) {
    const {x, y} = pos.get(l.name), cls = l.st.cls, out = l.altOf && !onRoute.has(l.name);
    const fill = out ? "var(--surface)" : cls === "done" ? "var(--good)" : cls === "failed" ? "var(--crit)" : cls === "blocked" ? "var(--warn)" : "var(--surface)";
    const stroke = out ? "var(--axis)" : cls === "working" ? "var(--fill)" : cls === "done" ? "var(--good)" : cls === "blocked" ? "var(--warn)" : cls === "failed" ? "var(--crit)" : "var(--axis)";
    const room = Math.max(4, Math.floor(seg / 6.6)), name = l.name.length > room ? l.name.slice(0, room - 1) + "…" : l.name;
    const tip = l.altOf ? `${l.name}: alternate for ${l.altOf}, ${out ? "not taken" : `taken: ${l.st.label}`}` : `${l.name}: ${l.st.label}`;
    const dash = l.altOf ? ";stroke-dasharray:2 2" : l.st.planned ? ";stroke-dasharray:3 2" : "";
    stops.push(`<g${l.w ? ` data-href="#/w/${esc(l.w.name)}"` : ""} class="stop${l.altOf ? " alt" : ""}${out ? " out" : ""}"><title>${esc(tip)}</title>
      ${!join && ends.length === 1 && ends[0] === l ? `<circle cx="${x}" cy="${y}" r="11" style="fill:none;stroke:${stroke};stroke-width:1.5"/>` : ""}
      <circle cx="${x}" cy="${y}" r="6.5" style="fill:${fill};stroke:${stroke};stroke-width:2${dash}"/>
      <text x="${x}" y="${y + 22}" text-anchor="middle">${esc(name)}</text></g>`);
  }
  if (dest) {
    const stroke = landed ? "var(--good)" : "var(--axis)";
    stops.push(`<g class="stop"><title>destination: every lane joins here</title><circle cx="${dest.x}" cy="${dest.y}" r="11" style="fill:none;stroke:${stroke};stroke-width:1.5"/>
      <circle cx="${dest.x}" cy="${dest.y}" r="4" style="fill:${stroke}"/></g>`);
  }
  const gateHtml = `<rect x="${X0 - 4}" y="${Y - 4}" width="8" height="8" rx="2" style="fill:var(--axis)"/><text x="${X0}" y="${Y + 22}" text-anchor="middle">gate</text>`;
  const planesHtml = planes.map(pl => `<g transform="translate(${pl.x.toFixed(1)},${pl.y.toFixed(1)})"><g${pl.up ? ` class="bob"` : ""}><path d="${PLANE}" style="fill:${pl.color};stroke:var(--page);stroke-width:1"/></g></g>`).join("");
  const svg = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(status.replace(/<[^>]+>/g, ""))}">${tracks.join("")}${gateHtml}${stops.join("")}${planesHtml}</svg>`;
  return `<div class="flight" data-plan="${esc(p.sessionId)}" data-order="${esc(p.sessionId)}" title="Open its flight plan">
    <div class="fhead">${reorderHtml(p, s)}<b>${p.flight ? esc(p.flight) : "Flight"}</b> ${connectsHtml(p)} ${esc(p.title)} ${p.hold ? holdChip(p, holdReady(p)) : ""}<span class="sub">· ${status}${flightGit(p, s)}</span>${p.times?.departed ? ` · ${timesLine(p.times)}` : ""}</div>${svg}</div>`;
}

// ------------------------------------------------------------------ the controller's order of flights

// Flights list in the controller's priority order, pilot.py's priority.json, the strips and the plan cards alike.
// A flight's grip drags it up or down its list: pointer events, so a finger works as a mouse does (the grip takes
// no scrolling touch). Its ▲▼ buttons move it one place, for a keyboard or a hand that would rather tap. Either
// sends the whole order to POST /api/priority; the list shows it at once and redraws from the server's state.
let dragging = null;

function reorderHtml(p, s) {
  if ((s.plans || []).length < 2) return "";
  const sid = esc(p.sessionId);
  return `<span class="reorder"><span class="grip" data-grip="${sid}" title="Drag to reorder flights by priority">⠿</span><button class="mv" data-move="${sid}" data-dir="-1" title="Up: higher priority" aria-label="move up">▲</button><button class="mv" data-move="${sid}" data-dir="1" title="Down: lower priority" aria-label="move down">▼</button></span>`;
}

async function setOrder(ids) {
  const was = S.plans;
  S.plans = ids.map(id => was.find(p => p.sessionId === id)).filter(Boolean);
  render();
  try { await post("/api/priority", {order: ids}); }
  catch (e) { S.plans = was; render(); alert(e.message); }
}

function moveFlight(sid, dir) {
  const ids = (S.plans || []).map(p => p.sessionId), i = ids.indexOf(sid), j = i + dir;
  if (i < 0 || j < 0 || j >= ids.length) return;
  [ids[i], ids[j]] = [ids[j], ids[i]];
  setOrder(ids);
}

document.addEventListener("pointerdown", e => {
  const g = e.target.closest("[data-grip]"), item = g?.closest("[data-order]");
  if (!item || e.button) return;
  e.preventDefault();
  g.setPointerCapture(e.pointerId);
  dragging = {item, list: item.parentElement, from: [...item.parentElement.children].map(c => c.dataset.order).join()};
  item.classList.add("dragging");
});
document.addEventListener("pointermove", e => {
  if (!dragging) return;
  const {item, list} = dragging;
  // It goes before the first other flight whose middle is below the pointer, or last.
  const next = [...list.querySelectorAll(":scope > [data-order]")].find(c => c !== item && e.clientY < c.getBoundingClientRect().top + c.offsetHeight / 2);
  if (next !== item.nextElementSibling) list.insertBefore(item, next || null);
});
function drop() {
  if (!dragging) return;
  const {item, list, from} = dragging, ids = [...list.querySelectorAll(":scope > [data-order]")].map(c => c.dataset.order);
  item.classList.remove("dragging");
  dragging = null;
  list._html = null;                  // its markup no longer matches what was drawn: the next render redraws it
  if (ids.join() !== from) setOrder(ids); else render();
}
document.addEventListener("pointerup", drop);
document.addEventListener("pointercancel", drop);
// The grip only drags: a click on it neither opens the flight's plan nor folds its card.
document.addEventListener("click", e => { if (e.target.closest("[data-grip]")) { e.preventDefault(); e.stopPropagation(); } }, true);

function flightsHtml(s) {
  const plans = s.plans || [];
  return plans.length ? plans.map(p => flightStrip(p, s)).join("")
    : `<div class="empty">No flights. A pilot files one with <code>pilot plan</code>; each leg of its route becomes a stop here.</div>`;
}

// ------------------------------------------------------------------ views

const VIEWS = {};

VIEWS.overview = (root, r) => {
  // #/plan/<pilot session>: the Overview with that plan opened and scrolled to, once it is there to show.
  let focusPlan = r?.plan || null;
  if (focusPlan) opened.set("plan:" + focusPlan, true);
  root.innerHTML = `
    <div class="s-plans"></div>
    <div class="card pad bcast"><b>Intercom</b>
      <textarea class="s-btext" rows="1" placeholder="A message to live sessions: it reaches each at its next step, mid-turn too. Ctrl+Enter sends."></textarea>
      <select class="s-bto"></select><button class="primary s-bsend">Send</button><div class="sub s-bnote"></div></div>
    <h2>Flights</h2><div class="card flights s-flights"></div>
    <h2>Summary</h2>
    <div class="card pad"><div class="cardhead"><b>All workers</b><span><span class="sub s-stamp"></span> <button data-sum="*" class="s-btn">Summarise</button></span></div>
      <div class="summary s-sum" style="margin-top:6px"></div></div>
    <h2>Workers</h2><div class="card"><div class="list s-workers"></div>
      <div class="empty s-wempty">No workers. The pilot starts them with <code>pilot start NAME --brief FILE</code>.</div></div>
    <h2>Sessions in the main checkout</h2><div class="card"><div class="list s-pilots"></div>
      <div class="empty s-pempty">No session open in a repo's main checkout. Start the pilot there with <code>pilot</code>.</div></div>`;
  const rowsW = new Map(), rowsP = new Map();
  // The intercom's targets: everyone, all pilots, all workers, a flight's pilot and legs, or one session.
  let targets = [], picked = "everyone";
  const bto = $(".s-bto", root), btext = $(".s-btext", root), bnote = $(".s-bnote", root);
  bto.addEventListener("change", () => { picked = bto.value; });
  async function broadcast() {
    const t = targets.find(([label]) => label === bto.value), text = btext.value.trim();
    if (!t || !text) return;
    $(".s-bsend", root).disabled = true; put(bnote, `sending to ${esc(t[1].join(", "))}…`);
    try { const j = await post("/api/intercom", {to: t[1], text}); btext.value = ""; put(bnote, esc(j.result)); }
    catch (e) { put(bnote, `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
    $(".s-bsend", root).disabled = false;
  }
  $(".s-bsend", root).addEventListener("click", broadcast);
  btext.addEventListener("keydown", e => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); broadcast(); } });
  return {
    update(s) {
      const pilotsN = s.pilots.map(p => p.name), workersN = s.workers.filter(w => w.pid).map(w => w.agent);
      targets = [];
      if (pilotsN.length && workersN.length) targets.push(["everyone", [...pilotsN, ...workersN]]);
      if (pilotsN.length) targets.push(["all pilots", pilotsN]);
      if (workersN.length) targets.push(["all workers", workersN]);
      for (const p of s.plans || []) {
        const legs = p.legs.map(l => s.workers.find(w => w.name === l.leg && w.pid)).filter(Boolean).map(w => w.agent);
        if (p.pilot && pilotsN.includes(p.pilot) && legs.length) targets.push([`${p.title}: pilot and legs`, [p.pilot, ...legs]]);
      }
      for (const n of [...pilotsN, ...workersN]) targets.push([n, [n]]);
      if (!targets.some(([l]) => l === picked) && targets.length) picked = targets[0][0];
      put(bto, targets.map(([l, ns]) => `<option${l === picked ? " selected" : ""} value="${esc(l)}">${esc(l)}${ns.length > 1 ? ` (${ns.length})` : ""}</option>`).join("")
        || `<option value="">no live sessions</option>`);
      $(".s-bsend", root).disabled = !targets.length;
      const sp = summaryParts("*", s);
      put($(".s-sum", root), sp.body); put($(".s-stamp", root), esc(sp.stamp)); $(".s-btn", root).disabled = sp.running;
      put($(".s-plans", root), (s.plans || []).length ? s.plans.map(p => planCard(p, s)).join("")
        : `<div class="card pad"><b>No flight plan</b><div class="sub" style="margin-top:4px">The pilot opens one with <code>pilot plan</code> before it starts workers: the objective, the route of legs, and a log you can both read.</div></div>`);
      const card = focusPlan && document.getElementById("plan-" + focusPlan);
      // Below the sticky top bar, whose height depends on how it wraps.
      if (card) { window.scrollTo(0, card.getBoundingClientRect().top + scrollY - ($(".top")?.offsetHeight || 0) - 8); focusPlan = null; }
      put($(".s-flights", root), flightsHtml(s));
      place($(".s-workers", root), s.workers.map(w => ({key: w.name, href: `#/w/${w.name}`, fill: v => {
        put(v.name, `${esc(w.name)}<small>${esc(w.branch)} · ${esc(w.id)} · ${esc(ago(w.lastActivity))}</small>`);
        put(v.chip, chip(w)); put(v.meter, meter(w.spend, w.budget)); put(v.doing, doing(w)); put(v.actions, actions(w, true));
      }})), $(".s-wempty", root), rowsW);
      place($(".s-pilots", root), s.pilots.map(p => ({key: p.sessionId, href: `#/s/${p.sessionId}`, fill: v => {
        put(v.name, `${esc(p.name)}<small>${esc(p.kind)} · ${esc(ago(p.lastActivity))}</small>`);
        put(v.chip, pilotChip(p)); put(v.meter, meter(p.spend, 0));
        put(v.doing, p.status === "waiting" ? `<span class="doing"><b>needs you:</b> ${esc(p.ask?.questions?.[0]?.question || p.waitingFor || "input")}</span>`
          : p.now ? `<span class="doing">${nowText(p.now)}</span>` : "");
        put(v.actions, actions(p, false));
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
      <div><div class="card"><div class="stream tstream s-stream"><div class="empty">loading…</div></div><div class="live s-live" hidden></div></div>
        <details class="card pad term s-term" hidden><summary><b>Terminal</b> <span class="sub">its screen, read every 2 s while this is open; what you type goes straight into it</span></summary>
          <div class="sub s-ttitle"></div><pre class="screen s-screen">reading…</pre>
          <div class="termrow"><input class="s-ttext" placeholder="text to type, e.g. an answer or /remote-control">
            <label class="sub"><input type="checkbox" class="s-tenter" checked> then Enter</label><button class="primary s-tsend">Type</button>
            <span class="keys"><button data-tkey="up" title="Up arrow">↑</button><button data-tkey="down" title="Down arrow">↓</button><button data-tkey="enter">Enter</button><button data-tkey="esc" title="Esc cancels a prompt, and mid-turn it interrupts the turn">Esc</button></span></div>
          <div class="sub s-tnote"></div></details>
        <div class="say s-say" hidden><textarea class="s-text" placeholder="Message this session. Ctrl+Enter sends."></textarea>
          <div class="bar2"><span class="sub s-hint"></span><label class="sub s-asturn" hidden title="Stops it and continues it with your words as a user turn, in a new session id. Only once its turn has ended."><input type="checkbox" class="s-asturnbox"> as my own turn</label>
            <button class="primary s-send">Send</button></div></div></div>
      <div class="side"><div class="card pad s-ctl" hidden><b>Controls</b>
          <div class="ctlrow"><label>budget, weighted tokens <input class="s-budget" inputmode="numeric" placeholder="e.g. 20M"></label><button class="s-setbudget">set</button></div>
          <div class="ctlrow s-fill"></div>
          <div class="sub s-ctlhint"></div></div>
        <div class="card pad s-checkcard" hidden><div class="cardhead"><b>Checklist</b><span class="sub s-checkn"></span></div><ul class="checklist s-check"></ul></div>
        <div class="card pad s-gitcard" hidden><div class="cardhead"><b>What it leaves</b><span class="sub s-gitn"></span></div><dl class="s-git" style="margin-top:8px"></dl></div>
        <div class="card pad"><b>Session</b><dl class="s-info" style="margin-top:8px"></dl></div>
        <div class="card pad"><b>Subagents</b><div class="s-agents" style="margin-top:6px"><span class="sub">none</span></div></div></div>
    </div>`;
  const stream = $(".s-stream", root), tools = new Map();
  let sid, es = null, agentsRev = null, worker = null, sayTo = null, turnAt = null, liveText = "", agentLabel = "", titleName = "";

  function open(newSid) {
    if (es) es.close();
    sid = newSid; tools.clear(); agentsRev = null; turnAt = null;
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

  // The conversation as Claude Code's terminal draws it, ported from Sam's campaigner (its Term.tsx): ● what
  // the session says; one line per tool call, `Name(what it was asked)`, its dot green, red, or blinking while
  // it runs, and its result under a ⎿, three lines of it and the rest a click away (a Read says only how many
  // lines); thinking folded to one line; a subagent on its own page, linked from the call that started it.
  // A click on a call's line shows its whole input. A call pilot's wiring refused and the session answered by
  // doing what it said (`gate`: pilot's ask hook, Sam's reply gate) is not drawn: that is the wiring talking.
  const PREVIEW = 3;
  const plain = s => esc(s.replace(/\x1b\[[0-9;]*m/g, ""));
  function row(dot, cls, html) {
    const div = document.createElement("div");
    div.className = "ev t-row" + (cls ? " " + cls : "");
    div.innerHTML = `<span class="t-dot">${dot}</span><div class="t-body">${html}</div>`;
    return div;
  }
  const agentLink = id => `<a class="t-link" href="#/s/${sid}/a/${esc(id)}">open subagent ${esc(id.slice(0, 8))} ›</a>`;
  // mcp__<server>__<tool>, as the terminal names it: the tool, then the server. An Agent call by its subagent type and task.
  function callOf(ev) {
    let inp = {};
    try { inp = JSON.parse(ev.input || "{}") || {}; } catch { }
    if (ev.tool === "Agent" || ev.tool === "Task") return {name: inp.subagent_type || ev.tool, args: inp.description || ev.summary || ""};
    const mcp = /^mcp__(.+?)__(.+)$/.exec(ev.tool || "");
    return {name: mcp ? `${mcp[2]} (${mcp[1].replace(/^plugin_|^claude_ai_/, "").replace(/_/g, " ")})` : ev.tool || "tool", args: ev.summary || ""};
  }
  // Long text cut to its first lines until clicked: `.t-head` shows, `.t-full` waits, a [data-fold] swaps them.
  const folded = (head, more, full) => `<div class="t-head">${head}${more ? `<button class="t-fold" data-fold>${more}</button>` : ""}</div>
    <div class="t-full" hidden>${full}<button class="t-fold" data-fold>(click to collapse)</button></div>`;

  function event(ev) {
    if (ev.kind === "tool") {
      const c = callOf(ev);
      const div = row("●", "t-tool run", `<div class="t-call" title="${esc(clock(ev.at))} · click for its input"><b>${esc(c.name)}</b>${c.args
        ? `<span>(${esc(c.args.split("\n")[0])})</span>` : ""}</div><div class="t-agent">${ev.agentId ? agentLink(ev.agentId) : ""}</div>
        ${ev.input ? `<div class="t-input" hidden><pre>${esc(ev.input)}</pre></div>` : ""}<div class="t-res" hidden></div>`);
      tools.set(ev.id, {el: div, ev});
      return div;
    }
    if (ev.kind === "thinking")
      return row("✻", "t-think", `<details><summary>Thinking…</summary><div class="t-thought">${md(ev.text)}</div></details>`);
    if (ev.kind === "result") {
      const t = tools.get(ev.id);
      if (t) {
        if (ev.gate) { t.el.remove(); return null; }
        const bad = ev.error || !!ev.denied;
        t.el.classList.replace("run", bad ? "err" : "ok");
        const host = $(".t-res", t.el);
        host.className = "t-res" + (bad ? " err" : ""); host.hidden = false;
        host.innerHTML = result(ev, t.ev);
        if (ev.agentId && !t.ev.agentId) $(".t-agent", t.el).innerHTML = agentLink(ev.agentId);
        return null;
      }
      // Its call came before the backlog began: only a refusal is worth showing alone.
      return ev.denied && !ev.gate ? row("●", "t-tool err", `<div class="t-call"><b>refused</b></div><div class="t-res err">${result(ev, null)}</div>`) : null;
    }
    if (["prompt", "message", "handback", "notice"].includes(ev.kind)) turnAt = Date.parse(ev.at) || turnAt;
    const div = document.createElement("div");
    div.className = "ev " + ev.kind;
    div.title = clock(ev.at);
    switch (ev.kind) {
      case "prompt": {
        const parts = ev.text.replace(/\r/g, "").split("\n# Brief\n"), text = parts.slice(-1)[0], lines = text.split("\n");
        const body = lines.length > 12 ? folded(md(lines.slice(0, 8).join("\n")), `… +${lines.length - 8} lines (click to expand)`, md(text)) : md(text);
        return prompt(div, ev.queued ? "queued" : "", (parts.length > 1
          ? `<details><summary>worker instructions</summary><pre>${esc(parts.slice(0, -1).join("\n# Brief\n"))}</pre></details>` : "") + body);
      }
      case "message": return prompt(div, `message from ${ev.sender}`, md(ev.text));
      // A subagent's final report, handed back to this session: its words, not the user's.
      case "handback": return row("●", "t-handback", `<div class="t-label">report from a subagent${ev.agentId ? ` · ${agentLink(ev.agentId)}` : ""}</div>${md(ev.text)}`);
      case "said": return row("●", "said", md(ev.text));
      // `!` commands the user ran in the session, their output, and slash commands, as a terminal shows them.
      case "shell": div.innerHTML = `<div class="shellline"><span class="bang">!</span>${esc(ev.text)}</div>`; return div;
      case "command": div.innerHTML = `<div class="shellline cmd">${esc(ev.text)}</div>`; return div;
      case "shellout": {
        const text = [ev.text, ev.err && `\x1b[31m${ev.err}\x1b[0m`].filter(Boolean).join("\n"), n = (text.match(/\n/g) || []).length + 1;
        if (!text) return null;
        div.innerHTML = n > 14 ? `<details><summary>output · ${n} lines</summary><pre class="out">${ansi(text)}</pre></details>` : `<pre class="out">${ansi(text)}</pre>`;
        return div;
      }
      case "turn": div.innerHTML = `<hr>turn ended${ev.ms ? ` · ${Math.round(ev.ms / 1000)} s` : ""}`; return div;
      case "compact": div.innerHTML = `<hr>context compacted`; return div;
      case "notice": return row("●", "t-notice", esc(ev.text));
      case "recap": div.innerHTML = `<div class="meta">Claude Code's recap</div><div class="body">${md(ev.text)}</div>`; return div;
      case "error": return row("●", "t-error", esc(ev.text));
    }
    return null;
  }

  // A prompt, a message from another session: `>` and the words, as the terminal shows a user turn.
  function prompt(div, label, html) {
    div.classList.add("t-prompt");
    div.innerHTML = `<span class="t-gt">&gt;</span><div class="t-body">${label ? `<div class="t-label">${esc(label)}</div>` : ""}${html}</div>`;
    return div;
  }

  function result(ev, tool) {
    if (ev.denied) {
      let cmd = "";
      try { const inp = JSON.parse(tool?.input || "{}"); cmd = inp.command || inp.file_path || ""; } catch { cmd = tool?.input || ""; }
      return `<div class="denied"><div class="who">Refused by ${esc(ev.denied)}</div>${cmd ? `<pre>${esc(cmd)}</pre><button data-copytext="${esc(cmd)}">copy</button>` : ""}
        <details><summary>what it said</summary><pre>${esc(ev.text)}</pre></details></div>`;
    }
    const lines = ev.text.trim() ? ev.text.replace(/\s+$/, "").split("\n") : [];
    if (!lines.length) return `<div class="t-dim">(No output)</div>`;
    const read = tool?.tool === "Read" && !ev.error;
    const head = read ? [`Read ${lines.length.toLocaleString()} lines`] : lines.slice(0, PREVIEW), rest = lines.length - head.length;
    return folded(head.map(l => `<div class="t-line">${plain(l) || " "}</div>`).join(""),
      read ? "(click to expand)" : rest > 0 ? `… +${rest.toLocaleString()} lines (click to expand)` : "", `<pre>${ansi(ev.text)}</pre>`);
  }
  stream.addEventListener("click", e => {
    if (e.target.closest("a, [data-copytext], details > summary")) return;
    const fold = e.target.closest("[data-fold]");
    if (fold) {
      const host = fold.closest(".t-head, .t-full").parentElement;
      for (const el of host.querySelectorAll(":scope > .t-head, :scope > .t-full")) el.hidden = !el.hidden;
      return;
    }
    const input = e.target.closest(".t-call")?.parentElement.querySelector(":scope > .t-input");
    if (input) input.hidden = !input.hidden;
  });

  // A pilot's terminal: its screen, read while the card is open, and keystrokes typed into it after a
  // confirm. Only pilots: a background worker has no console. The server logs what is typed in the plan.
  const term = $(".s-term", root);
  let termSid = null, termTimer = null, termName = "";
  async function readTerm() {
    if (!termSid || !term.open) return;
    try {
      const j = await (await fetch(`/api/console/${termSid}`)).json();
      if (j.error) { put($(".s-tnote", root), `<span style="color:var(--crit)">${esc(j.error)}</span>`); return; }
      put($(".s-ttitle", root), `window title: ${esc(j.title)}`);
      const pre = $(".s-screen", root), stick = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 30;
      put(pre, j.rows ? screenHtml(j.rows) : esc(j.lines.join("\n")));
      if (stick) pre.scrollTop = pre.scrollHeight;
    } catch (e) { put($(".s-tnote", root), esc(e.message)); }
  }
  async function typeTerm(text, keys) {
    const what = [text && `"${text}"`, ...keys.map(k => k === "enter" ? "Enter" : k === "esc" ? "Esc" : k === "up" ? "↑" : "↓")].filter(Boolean).join(" + ");
    if (!(keys.length === 1 && (keys[0] === "up" || keys[0] === "down")) && !confirm(`Type ${what} into ${termName}'s terminal?`)) return;
    put($(".s-tnote", root), "typing…");
    try {
      const j = await post(`/api/console/${termSid}`, {text, keys});
      put($(".s-screen", root), j.rows ? screenHtml(j.rows) : esc(j.lines.join("\n"))); $(".s-screen", root).scrollTop = 1e9;
      put($(".s-tnote", root), `typed ${esc(what)}`);
      $(".s-ttext", root).value = "";
    } catch (e) { put($(".s-tnote", root), `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
  }
  term.addEventListener("toggle", () => { clearInterval(termTimer); if (term.open) { readTerm(); termTimer = setInterval(readTerm, 2000); } });
  $(".s-tsend", root).addEventListener("click", () => {
    const text = $(".s-ttext", root).value.trim();
    if (text || $(".s-tenter", root).checked) typeTerm(text, $(".s-tenter", root).checked ? ["enter"] : []);
  });
  $(".s-ttext", root).addEventListener("keydown", e => { if (e.key === "Enter") { e.preventDefault(); $(".s-tsend", root).click(); } });
  term.addEventListener("click", e => { const k = e.target.closest("[data-tkey]")?.dataset.tkey; if (k) typeTerm("", [k]); });

  function tick() {
    const el = $(".s-live", root);
    el.hidden = !liveText;
    if (!liveText) return;
    const s = turnAt ? Math.max(0, Math.floor((Date.now() - turnAt) / 1000)) : null;
    put(el, liveText + (s === null ? "" : ` <span class="sub">${s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`}</span>`));
  }
  const timer = setInterval(tick, 1000);

  const setTitle = () => put($(".s-title", root), esc(titleName) + (r.agent ? ` <span class="sub">› subagent ${esc(agentLabel || r.agent)}</span>` : ""));
  // Each subagent is a page of its own, as in campaigner: the main conversation is the session's alone. On a
  // subagent's page the card leads back to the session and marks the one shown.
  async function loadAgents(rev) {
    if (!sid || rev === agentsRev) return;
    agentsRev = rev;
    try {
      const list = await (await fetch(`/api/sessions/${sid}/agents`)).json(), cur = list.find(a => a.id === r.agent);
      agentLabel = cur ? `${cur.type || "agent"}${cur.description ? ` · ${cur.description}` : ""}` : "";
      setTitle();
      put($(".s-agents", root), (r.agent ? `<a class="agent" href="#/s/${sid}">the session itself<small>its main conversation</small></a>` : "")
        + (list.length ? list.sort((a, b) => b.lastActivity - a.lastActivity).map(a =>
          `<a class="agent${a.id === r.agent ? " on" : ""}" href="#/s/${sid}/a/${esc(a.id)}">${esc(a.description || a.id)}<small>${esc(a.type || "agent")} · ${esc(M(a.weighted))} · ${esc(ago(a.lastActivity))}</small></a>`).join("")
          : `<span class="sub">none</span>`));
    } catch { agentsRev = null; }
  }

  // Through the intercom by default: it reaches the session at its next step, mid-turn too, without stopping
  // it. "As my own turn" is the old way for a worker whose turn has ended: stop it, continue it with your words.
  async function send() {
    const box = $(".s-text", root), text = box.value.trim(), hint = $(".s-hint", root);
    if (!text || !(sayTo || worker)) return;
    const btn = $(".s-send", root), asTurn = !!worker && (!sayTo || (!$(".s-asturn", root).hidden && $(".s-asturnbox", root).checked));
    btn.disabled = true; put(hint, "delivering…");
    try {
      if (asTurn) {
        const j = await post(`/api/workers/${worker}/say`, {text});
        put(hint, `delivered as your turn; it continues as ${esc(j.id)}`);
      } else {
        const j = await post("/api/intercom", {to: [sayTo], text});
        put(hint, `intercom: ${esc(j.result)}`);
      }
      box.value = "";
    } catch (e) { put(hint, `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
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
      titleName = name; setTitle();
      stream.classList.toggle("idle", !(w ? w.state === "working" : p?.busy));
      put($(".s-chip", root), w ? chip(w) : p ? pilotChip(p) : h ? chip(h) : "");
      put($(".s-meter", root), w ? meter(w.spend, w.budget) : p ? meter(p.spend, 0) : h ? meter(h.final?.spend, 0) : "");
      put($(".s-actions", root), w ? actions(w, true) : p ? actions(p, false) : "");
      const plan = (s.plans || []).find(x => x.sessionId === (p ? p.sessionId : r.sid));
      put($(".s-plan", root), plan && !r.agent ? planCard(plan, s) : "");
      $(".s-ctl", root).hidden = !w;
      put($(".s-fill", root), w && w.budget ? fillButton(w) : "");
      const card = $(".s-sumcard", root);
      card.hidden = !w;
      if (w) {
        const sp = summaryParts(w.name, s);
        put($(".s-sum", root), sp.body); put($(".s-stamp", root), esc(sp.stamp)); $(".s-btn", root).disabled = sp.running;
      }
      const say = $(".s-say", root);
      sayTo = w && w.pid ? w.agent : p ? p.name : null;
      say.hidden = !(sayTo || w) || !!r.agent;
      const turnOver = !!w && !(w.pid && (w.state === "working" || w.state === "blocked"));
      $(".s-asturn", root).hidden = !turnOver;
      if (!sayTo && w) $(".s-asturnbox", root).checked = true;      // not running: only a new turn reaches it
      if (!$(".s-hint", root)._html) put($(".s-hint", root), sayTo ? "Through the intercom: it arrives at its next step, mid-turn too." : "");
      termSid = p && !r.agent ? p.sessionId : null; termName = p ? p.name : "";
      term.hidden = !termSid;
      const items = (w ? w.checklist : p ? p.checklist : h?.final?.checklist) || [], left = openItems(items).length;
      $(".s-checkcard", root).hidden = !items.length || !!r.agent;
      put($(".s-checkn", root), items.length ? `${items.length - left} of ${items.length} done${left && (w?.reportHead || h?.final?.reportHead) === "DONE" ? " · reported DONE with items open" : ""}` : "");
      put($(".s-check", root), checkList(items));
      const g = (w || h)?.git, outside = (w || h)?.outside;
      $(".s-gitcard", root).hidden = !(g || outside) || !!r.agent;
      put($(".s-gitn", root), gitLine(g, (w || h)?.approach));
      put($(".s-git", root), [["repo", g?.repo], ["branch", g && `${g.branch}${g.head ? ` at ${g.head}` : ""}${g.gone ? " (deleted)" : ""}`],
        ["commits", g && !g.gone ? `${g.ahead ?? "?"} past ${g.base}` : ""],
        ["not pushed", g?.unpushed ? `${g.unpushed} commit(s) on no remote branch` : ""],
        ["on remote", (g?.ahead && g?.remotes?.join(", ")) || ""],
        ["worktree", g && !g.gone ? (g.worktree ? (g.dirty ? `${g.dirty} uncommitted` : "clean") : "removed") : ""]]
        .filter(([, v]) => v).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join("")
        + (g?.dirtyFiles?.length ? `<dt>uncommitted</dt><dd><pre class="dirty">${esc(g.dirtyFiles.join("\n"))}</pre></dd>` : "")
        + `<dt>outside the worktree</dt><dd>${outside ? md(outside) : `<span class="sub">${w && w.state === "working" ? "its report will say" : "its report does not say"}</span>`}</dd>`);
      const info = w || h || p || {};
      const leg = (s.plans || []).flatMap(x => x.legs.map(l => ({...l, plan: x.title}))).find(l => l.leg === (w || h)?.name);
      const rows = [["flight plan", leg?.plan], ["leg goal", leg?.goal], ["done when", leg?.doneWhen],
        ["repo", info.repo], ["branch", info.branch], ["from", info.base], ["worktree", info.worktree], ["model", info.model || (w ? "account default" : "")],
        ["effort", info.effort], ["sessions", Array.isArray(info.sessions) ? info.sessions.length : info.sessions], ["pilot", info.pilot], ["after", (info.after || []).join(", ")],
        ["started", when(info.started)], ["removed", when(info.archivedAt)], ["Claude Code says", info.detail || info.final?.detail], ["session", sid]];
      put($(".s-info", root), rows.filter(([, v]) => v).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join(""));
      loadAgents(w ? w.rev : p ? p.rev : 0);
      // The live line: what it is doing now (Claude Code's own one-line detail for a worker), or what it is
      // waiting on you for, and how long this turn has run.
      const x = w || p, waitingOn = w ? waitsOnYou(w) : p?.status === "waiting";
      liveText = r.agent || !x ? ""
        : waitingOn ? `<span class="pulse wait"></span><b>waiting on you</b> ${esc(w ? w.detail || "" : p.ask?.questions?.[0]?.question || p.waitingFor || "")}`
        : (w ? w.state === "working" : p.busy) ? `<span class="pulse"></span>${w?.detail ? esc(w.detail) : x.now ? nowText(x.now) : "working"}` : "";
      if (!turnAt && w?.started) turnAt = w.started * 1000;    // the turn began before the backlog: since it started
      tick();
    },
    unmount() { if (es) es.close(); clearInterval(timer); clearInterval(termTimer); },
  };
};

// The black box: every decision and event across all flights, newest first, from <state>/blackbox.jsonl and
// the lines pilots wrote into their plans themselves. Filter by who acted and by flight.
VIEWS.blackbox = root => {
  root.innerHTML = `<h2>Black box</h2>
    <div class="card pad bbfilter"><span class="sub">who</span><span class="s-who"></span><span class="sub">flight</span><select class="s-flight"></select>
      <span class="sub s-count"></span></div>
    <div class="card tablewrap"><table class="bb"><thead><tr><th>When</th><th>Who</th><th>Flight</th><th>What</th></tr></thead><tbody class="s-rows"></tbody></table>
      <div class="empty s-empty" hidden>Nothing recorded yet.</div></div>`;
  let all = [], who = "all", flight = "all", fetched = 0;
  const WHO = ["all", "controller", "autopilot", "watchtower", "pilot", "pilot.py"];
  async function load() {
    if (Date.now() - fetched < 5000) return;
    fetched = Date.now();
    try { all = await (await fetch("/api/blackbox")).json(); draw(); } catch (e) { put($(".s-count", root), esc(e.message)); }
  }
  function draw() {
    const flights = ["all", ...new Set(all.map(e => e.flight).filter(Boolean))];
    put($(".s-who", root), WHO.map(w => `<button class="choice${w === who ? " on" : ""}" data-who="${w}">${w}</button>`).join(""));
    put($(".s-flight", root), flights.map(f => `<option${f === flight ? " selected" : ""}>${esc(f)}</option>`).join(""));
    const rows = all.filter(e => (who === "all" || e.actor === who) && (flight === "all" || e.flight === flight));
    put($(".s-count", root), `${rows.length} of ${all.length}`);
    put($(".s-rows", root), rows.slice(0, 500).map(e => `<tr><td class="nowrap">${esc(when(e.at))}</td><td><span class="who ${esc(e.actor)}">${esc(e.actor)}</span></td>
      <td>${esc(e.flight || "")}</td><td>${md(e.text, true)}</td></tr>`).join(""));
    $(".s-empty", root).hidden = rows.length > 0;
  }
  root.addEventListener("click", e => { const w = e.target.closest("[data-who]")?.dataset.who; if (w) { who = w; draw(); } });
  $(".s-flight", root).addEventListener("change", e => { flight = e.target.value; draw(); });
  return {update() { load(); }};
};

// Archived flights, pilot.py's flight_history(): plans the controller put away, kept whole, each with its times,
// how its legs ended and a Restore. Not the workers below them, which `pilot rm` archived one leg at a time.
function flightHistoryRow(f) {
  const t = f.times || {};
  const end = t.arrived ? `ARR ${esc(when(t.arrived))}` : t.eta ? `last ETA ${esc(when(t.eta))}` : esc(t.noEta || t.error || "no ETA");
  const CHIP = {good: "done", warn: "blocked", bad: "failed", working: "working"};   // leg_state's classes, as chips
  const legs = f.legs.map(l => `<span class="chip ${CHIP[l.cls] || "other"}" title="${esc(l.altOf ? `alternate for ${l.altOf}` : "")}">${esc(l.leg)}: ${esc(l.label)}</span>`).join(" ");
  return `<tr><td><b>${f.flight ? `Flight ${esc(f.flight)}` : "Flight plan"}</b><div class="sub">${esc(f.title)}</div></td>
    <td>${t.departed ? `DEP ${esc(when(t.departed))}` : `<span class="sub">not departed</span>`}<div class="sub">${end}</div></td>
    <td>${t.delay != null ? delayHtml(t.delay) : `<span class="sub">-</span>`}</td>
    <td>${legs || `<span class="sub">no legs</span>`}${f.flyingAtArchive?.length ? `<div class="sub">archived with ${esc(f.flyingAtArchive.join(", "))} still flying</div>` : ""}</td>
    <td>${esc(when(f.archivedAt))}</td>
    <td>${f.missing ? "" : `<button data-restore="${esc(f.sessionId)}" title="Back on the Overview, in its old place in the order">Restore</button> <a href="#/s/${esc(f.sessionId)}">pilot session ›</a>`}</td></tr>`;
}

VIEWS.history = root => {
  root.innerHTML = `<h2>Flights</h2><div class="card tablewrap"><table><thead><tr><th>Flight</th><th>Times</th><th>Delay</th><th>Legs</th><th>Archived</th><th></th></tr></thead>
    <tbody class="s-flights"></tbody></table><div class="empty s-fempty">Flights you archive from their plan card, or with <code>pilot archive &lt;flight&gt;</code>, are kept here whole, to restore.</div></div>
    <h2>Workers</h2><div class="card tablewrap"><table><thead><tr><th>Worker</th><th>Result</th><th>Branch</th><th class="num">Spend</th><th>Removed</th><th></th></tr></thead>
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
      const fl = s.flightHistory || [];
      put($(".s-flights", root), fl.map(flightHistoryRow).join(""));
      $(".s-fempty", root).hidden = fl.length > 0;
      put($(".s-rows", root), s.history.map(h => {
        const key = `${h.name}-${h.archivedAt}`, f = h.final || {};
        return `<tr class="click" data-key="${esc(key)}"><td><b>${esc(h.name)}</b><div class="sub">${esc(h.repo)}</div></td><td>${chip(h)}<div class="sub">${esc(f.detail || "")}</div></td>
          <td>${esc(h.branch)}<div class="sub">from ${esc(h.base || "")}</div>${gitLine(h.git)}</td><td class="num">${esc(M(f.spend?.weighted))}</td><td>${esc(when(h.archivedAt))}</td>
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
      <label>Project <input class="s-filter" type="search" placeholder="filter: every word must match, e.g. sharp dir" autocomplete="off">
        <select class="s-proj"><option>loading…</option></select></label>
      <div class="sub s-projnote"></div>
      <div><b>Session</b><div class="s-sessions sessions"></div></div>
      <div class="opts"><b>Where</b>
        <label><input type="radio" name="where" value="terminal" checked> a new Windows Terminal tab: interactive, you type into it</label>
        <label><input type="radio" name="where" value="background"> in the background: reach it with attach or Remote Control</label></div>
      <label>First message (optional)<textarea class="s-msg" placeholder="What this flight is for. Sent as the session's first prompt."></textarea></label>
      <label class="inline"><input type="checkbox" class="s-aspilot" checked> start it as the pilot (<code>/pilot:pilot</code>)</label>
      <label class="inline"><input type="checkbox" class="s-hold"> plan only, wait for clearance: the pilot plans the flight, then holds at the gate until you clear it for takeoff</label>
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
  // The project list, narrowed by the filter: every word typed must appear in the path, in any case.
  // The selection stays when it still matches; otherwise the first match is taken and its sessions load.
  function fill(want) {
    const words = $(".s-filter", root).value.toLowerCase().split(/\s+/).filter(Boolean);
    const shown = projects.filter(p => words.every(w => p.path.toLowerCase().includes(w)));
    const keep = shown.some(p => p.path === want) ? want : shown[0]?.path;
    sel.innerHTML = shown.length ? shown.map(p => `<option value="${esc(p.path)}"${p.path === keep ? " selected" : ""}>${esc(p.path)}${p.trusted ? "" : " (not trusted)"}</option>`).join("")
      : `<option value="">no project matches</option>`;
    $(".s-go", root).disabled = !shown.length;
    if (!shown.length) { put($(".s-projnote", root), ""); list.innerHTML = ""; }
    else if (keep !== want || !list.innerHTML) loadSessions();
  }
  (async () => {
    projects = await (await fetch("/api/projects")).json().catch(() => []);
    fill(new URLSearchParams(location.hash.split("?")[1] || "").get("path"));
    $(".s-filter", root).focus();
  })();
  $(".s-filter", root).addEventListener("input", () => fill(sel.value));
  // Plan only holds a pilot's flight: without the pilot it means nothing.
  $(".s-aspilot", root).addEventListener("change", e => { $(".s-hold", root).disabled = !e.target.checked; if (!e.target.checked) $(".s-hold", root).checked = false; });
  sel.addEventListener("change", loadSessions);
  // After a launch, the page waits for the session to come up, then goes to its flight plan once the pilot has
  // opened one, or to its session page when it was not started as the pilot. A new session is the first in that
  // directory started after the launch; a resumed one keeps its id; a background one is known by its job id.
  let pending = null;
  const norm = p => (p || "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
  $(".s-go", root).addEventListener("click", async () => {
    const btn = $(".s-go", root);
    btn.disabled = true; put($(".s-result", root), "launching…");
    try {
      const resume = $('input[name="sess"]:checked', root)?.value || null, asPilot = $(".s-aspilot", root).checked;
      const j = await post("/api/launch", {path: sel.value, resume, where: $('input[name="where"]:checked', root).value,
        message: $(".s-msg", root).value, asPilot, hold: asPilot && $(".s-hold", root).checked});
      pending = {path: norm(sel.value), resume, job: j.id || null, asPilot, at: Date.now(), where: j.where};
      put($(".s-result", root), (j.where === "terminal" ? "opened in a new Windows Terminal tab"
        : `started in the background as ${esc(j.id)}: <button data-copy="claude attach ${esc(j.id)}">copy attach</button>`)
        + (j.registered ? `; added to <code>~/.pilot/pilot.json</code> as ${esc(j.registered)}` : "") + "; waiting for it to come up…");
    } catch (e) { put($(".s-result", root), `<span style="color:var(--crit)">${esc(e.message)}</span>`); }
    btn.disabled = false;
  });
  return {update(s) {
    if (!pending) return;
    const p = s.pilots.find(x => pending.resume ? x.sessionId === pending.resume && (x.startedAt || 0) >= pending.at - 5000
      : pending.job ? x.id === pending.job || x.id?.startsWith(pending.job)
      : norm(x.cwd) === pending.path && (x.startedAt || 0) >= pending.at - 5000);
    const waited = (Date.now() - pending.at) / 1000;
    if (!p) {
      if (waited > 45) put($(".s-result", root), `launched, but no session has come up in that directory after ${Math.round(waited)} s. The dashboard lists sessions only in repos named in <code>~/.pilot/pilot.json</code>.`);
      return;
    }
    const plan = (s.plans || []).find(x => x.sessionId === p.sessionId);
    if (plan || !pending.asPilot || waited > 180) { pending = null; location.hash = plan ? `#/plan/${p.sessionId}` : `#/s/${p.sessionId}`; return; }
    put($(".s-result", root), `${esc(p.name)} is up; waiting for it to open its flight plan… <a href="#/s/${esc(p.sessionId)}">its session ›</a>`);
  }};
};

// ------------------------------------------------------------------ routing

function parse() {
  const h = location.hash.replace(/^#/, "") || "/";
  let m;
  if ((m = /^\/w\/([a-z0-9-]+)$/.exec(h))) return {view: "session", worker: m[1]};
  if ((m = /^\/s\/([0-9a-f-]{36})(?:\/a\/([0-9a-f]+))?$/.exec(h))) return {view: "session", sid: m[1], agent: m[2]};
  if (h === "/history") return {view: "history"};
  if (h === "/blackbox") return {view: "blackbox"};
  if (h === "/spend") return {view: "spend"};
  if (h.startsWith("/launch")) return {view: "launch"};
  if ((m = /^\/plan\/([0-9a-f-]{36})$/.exec(h))) return {view: "overview", plan: m[1]};
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
  if (S?.workers) current.view.update(S);
}

async function summarise(key) {
  delete sumErr[key];
  try { await post(`/api/summary/${key}`); } catch (e) { sumErr[key] = `Summary failed: ${e.message}`; }
  render();
}

// Restarts the server on the code now on disk (its supervisor starts it again), then reloads the page once
// the new server answers, so the page's code is the latest too.
async function restartServer(btn) {
  btn.disabled = true; btn.textContent = "restarting…";
  try { await post("/api/restart"); } catch (e) { btn.textContent = e.message; return; }
  await new Promise(r => setTimeout(r, 1000));
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch("/api/state", {cache: "no-store"})).ok) return location.reload(); } catch {}
    await new Promise(r => setTimeout(r, 500));
  }
  btn.textContent = "it has not come back: look at its terminal";
}

// A flight on the Flights strip: unfold its plan's card and scroll to it.
function openPlan(sid) {
  opened.set("plan:" + sid, true);
  render();
  const card = document.getElementById("plan-" + sid);
  if (card) window.scrollTo(0, card.getBoundingClientRect().top + scrollY - ($(".top")?.offsetHeight || 0) - 8);
}

document.addEventListener("click", async e => {
  const t = e.target.closest("button, a, [data-href], [data-plan]");
  if (!t) return;
  if (t.dataset.move) { e.preventDefault(); return moveFlight(t.dataset.move, Number(t.dataset.dir)); }   // not the card's fold too
  if (t.dataset.plan) { e.preventDefault(); return openPlan(t.dataset.plan); }   // a connection's chip folds no card it sits on
  if (t.classList.contains("s-restart")) return restartServer(t);
  if (t.classList.contains("s-clearlock")) {
    if (!confirm("Remove Claude Code's stale daemon lock? The next background session then starts a new daemon.")) return;
    t.disabled = true;
    try { await post("/api/daemon-lock/clear", {pid: Number(t.dataset.pid), procStartFt: t.dataset.ft}); }
    catch (e) { t.textContent = e.message; }
    return;
  }
  if (t.dataset.jump) {          // a superseded notice's link to the notice that superseded it, in the bell
    e.preventDefault();
    const to = $(`#notes .note[data-note="${CSS.escape(t.dataset.jump)}"]`);
    if (to) { to.scrollIntoView({block: "nearest"}); to.classList.add("flash"); setTimeout(() => to.classList.remove("flash"), 1200); }
    return;
  }
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
  // Archiving a flight is the controller's: one confirm, and a second that names its legs still flying.
  if (t.dataset.archive) {
    e.preventDefault();
    const sid = t.dataset.archive, name = t.dataset.name;
    if (!confirm(`Archive ${name}? It leaves the Overview and the Flights strips and is listed on History, where Restore puts it back. Nothing is deleted.`)) return;
    t.disabled = true;
    try { await post(`/api/flights/${sid}/archive`); }
    catch (err) {
      const legs = err.flying;
      if (legs?.length && confirm(`${name} has legs still flying: ${legs.join(", ")}. Archive it anyway and give up on them? They keep running until you stop them.`)) {
        try { await post(`/api/flights/${sid}/archive`, {force: true}); } catch (err2) { alert(err2.message); }
      } else if (!legs?.length) alert(err.message);
    } finally { t.disabled = false; }
    return;
  }
  // Clearing a flight held at the gate is the controller's: one confirm, then the hold is lifted and its pilot told.
  if (t.dataset.clear) {
    e.preventDefault();
    if (!confirm(`Clear ${t.dataset.name} for takeoff? The hold is lifted and its pilot is told to take off (woken if its session has ended).`)) return;
    t.disabled = true; t.textContent = "clearing…";
    try { const j = await post(`/api/flights/${t.dataset.clear}/clear`); t.textContent = `cleared: ${j.reached}`; }
    catch (err) { alert(err.message); t.disabled = false; t.textContent = "Cleared for takeoff"; }
    return;
  }
  if (t.dataset.restore) {
    t.disabled = true;
    try { await post(`/api/flights/${t.dataset.restore}/restore`); } catch (err) { alert(err.message); t.disabled = false; }
    return;
  }
  if (t.dataset.sum) { summarise(t.dataset.sum); return; }
  if (t.dataset.answer && t.dataset.kind) {        // a choice toggles; one choice per item
    const p = (S.plans || []).find(x => x.sessionId === t.dataset.answer), it = p && waitingItems(p.waiting).items[+t.dataset.item];
    if (!it) return;
    const k = `${p.sessionId}|${it.title}`, a = answers.get(k);
    if (a && a.kind === t.dataset.kind && a.value === t.dataset.value) answers.delete(k); else answers.set(k, {kind: t.dataset.kind, value: t.dataset.value});
    sentNote.delete(p.sessionId); render();
    return;
  }
  if (t.dataset.sendanswers) { sendAnswers(t.dataset.sendanswers); return; }
  if (t.dataset.merge) {
    // The controller's own merge: the dashboard runs gh pr merge --rebase, guarded on the PR's head, and tells the pilot.
    if (!confirm(`Rebase-merge PR #${t.dataset.pr}? The dashboard runs it now and tells the pilot.`)) return;
    t.disabled = true; t.textContent = "merging…";
    try { const j = await post("/api/merge", {plan: t.dataset.merge, pr: Number(t.dataset.pr)}); t.textContent = `merged #${j.merged} at ${j.head}`; }
    catch (err) { t.disabled = false; t.textContent = `Merge #${t.dataset.pr} (rebase)`; alert(err.message); }
    return;
  }
  if (t.dataset.refuel) {
    try { await post("/api/refuel", {mode: t.dataset.refuel}); } catch (err) { alert(err.message); }
    return;
  }
  if (t.dataset.fill) {
    const name = t.dataset.fill, to = +t.dataset.to;
    t.disabled = true;
    try {
      await post(`/api/workers/${name}/budget`, {tokens: to});
      // Fuel does not wake a worker: one whose turn ended at its budget waits until it hears from you.
      const w = S.workers.find(x => x.name === name);
      if (w && !(w.pid && (w.state === "working" || w.state === "blocked"))
          && confirm(`${name} now has ${M(to)}. Its turn has ended, so it waits until it hears from you. Tell it to carry on with its brief?`))
        await post(`/api/workers/${name}/say`, {text: `The controller raised your budget to ${M(to)} weighted tokens. Carry on with your brief `
          + `where you stopped. If nothing is left, end your turn with DONE and one line saying so.`});
    } catch (err) { alert(err.message); }
    t.disabled = false;
    return;
  }
  if (t.dataset.autopilot) {
    const on = t.dataset.autopilot === "on";
    if (on && !confirm("Switch the autopilot on? Until you switch it off, an Opus 5.5 session decides what waits on you in every live flight: "
      + "engineering calls, reviews, route changes within a flight's objective, refuels up to 3x. It never pushes, merges, deletes or sets a new objective; "
      + "those wait for you. Every decision goes in the black box.")) return;
    t.disabled = true; t.textContent = on ? "✈ autopilot: starting…" : "✈ autopilot: stopping…";
    try { await post("/api/autopilot", {on}); } catch (err) { alert(err.message); }
    t.disabled = false;
    return;
  }
  if (t.dataset.note) {
    post("/api/notifications/read", {ids: [t.dataset.note]}).catch(() => {});
    $("#panel").hidden = true;
  }
  if (t.tagName === "A" || t.tagName === "BUTTON") return;
  if (t.dataset.href && !getSelection().toString()) location.hash = t.dataset.href;
});
document.addEventListener("click", e => { if (!e.target.closest(".bellwrap")) $("#panel").hidden = true; }, true);
// A reply typed for a waiting item replaces that item's choice. It updates its neighbours in place rather
// than re-rendering, so the Send button you reach for next is not swapped out under the click.
document.addEventListener("input", e => {
  const el = e.target;
  if (!el.matches("input.reply[data-answer]")) return;
  const p = (S.plans || []).find(x => x.sessionId === el.dataset.answer), items = p ? waitingItems(p.waiting).items : [];
  const it = items[+el.dataset.item];
  if (!it) return;
  const k = `${p.sessionId}|${it.title}`;
  if (el.value.trim()) answers.set(k, {kind: "text", value: el.value.trim()}); else if (answers.get(k)?.kind === "text") answers.delete(k);
  if (el.value.trim()) for (const b of el.parentElement.querySelectorAll("button.choice")) b.classList.remove("on");
  const n = items.filter(x => answers.has(`${p.sessionId}|${x.title}`)).length, send = el.closest(".ask")?.querySelector("[data-sendanswers]");
  if (send) { send.disabled = !n || !(p.live && p.pilot); send.textContent = `Send ${n || ""} answer${n === 1 ? "" : "s"} to the pilot`; }
  sentNote.delete(p.sessionId);
});
window.addEventListener("hashchange", render);
setInterval(render, 30000);        // only the "5m ago" texts move between states
render();
connect();
