#!/usr/bin/env python
"""pilot: one pilot session and some workers, on Claude Code's own background sessions.

The pilot is an ordinary interactive session in the repo. A worker is `claude --bg -w <name>`: a background
session in its own git worktree, branched from the repo's default branch, which the Claude Code daemon keeps
running whether or not a terminal is open. This file starts workers the same way every time, lists them with
what they have spent, gates their spend, delivers your messages, summarises them and serves the dashboard.

  python pilot.py plan [--title T]     the pilot session's flight plan: its path, made from the template once
  python pilot.py start NAME --brief FILE|- [--repo R] [--model M] [--effort E] [--budget N]
                         [--pilot NAME] [--after NAME]...
  python pilot.py list [--json]
  python pilot.py result NAME          the worker's last message: its report
  python pilot.py say NAME TEXT|-      your message to a worker whose turn has ended, as a user turn
  python pilot.py budget NAME N        a new ceiling in weighted tokens (absolute, not an increment)
  python pilot.py summary [NAME]       a Haiku summary of one worker, or of all of them
  python pilot.py stop NAME            stop it; the conversation is kept
  python pilot.py rm NAME              delete it (its worktree too, when Claude Code says that is safe) and
                                       keep its record in the history
  python pilot.py dashboard [--port 5181]
  python pilot.py hook-budget          the worker plugin's PreToolUse hook (reads the payload on stdin)

Windows notes, all measured on CLI 2.1.296:
  * The brief goes to claude on stdin, never as an argument: a Windows command line stops at 32,767
    characters, and `-p "<prompt>"` with no stdin redirect waits 3 s for stdin first.
  * Options like --allowedTools, --add-dir and --tools take several values and swallow a prompt written
    after them; with the prompt on stdin there is nothing to swallow.
  * `--bg` refuses a directory whose workspace trust was never accepted. The repo must have been opened
    interactively once; `-w` worktrees inside it inherit that.
  * `--bg --resume <session>` always continues in a new session id, a copy holding the whole conversation,
    in the same worktree and with the same options. `say` uses that and moves the worker's record onto the
    copy. Only the session that made the worktree owns it, so `rm` removes the copies first.
"""
import argparse
import datetime
import glob
import http.server
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse
from pathlib import Path

HERE = Path(__file__).resolve().parent
WEB = HERE / "web"
CONFIG = Path.home() / ".pilot" / "pilot.json"   # this machine's repos and defaults: local paths, so not in the repo
STATE = Path(os.environ.get("PILOT_STATE") or Path(os.environ.get("LOCALAPPDATA") or Path.home() / ".local/state") / "pilot")
WORKERS = STATE / "workers"
ARCHIVE = STATE / "archive"
BRIEFS = STATE / "briefs"
METERS = STATE / "meters"
PLANS = STATE / "plans"
FLIGHTS = STATE / "flights.json"           # the last flight number given out, per repo code
ASKS = STATE / "asks"                       # the question a pilot is waiting on you for, by session id
NOTICES = STATE / "notifications.json"
SUMMARIES = STATE / "summaries.json"
PROFILE = Path(os.environ.get("CLAUDE_CONFIG_DIR") or Path.home() / ".claude")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,29}$")
QUIET_SECONDS = 30 * 60


def config():
    cfg = json.loads(CONFIG.read_text(encoding="utf-8")) if CONFIG.exists() else {}
    return {"repos": {}, "model": None, "effort": None, "budget": 10_000_000, **cfg}


def read_json(path, default=None):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def write_json(path, obj):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(obj, indent=1), encoding="utf-8")
    os.replace(tmp, path)


# --------------------------------------------------------------------------- claude

def claude_env():
    """This process's environment minus the parent session's CLAUDE* variables, so a worker started from
    inside the pilot session is a fresh top-level session rather than a nested child of it. The profile
    (CLAUDE_CONFIG_DIR) is kept: it is the one account everything here runs on."""
    return {k: v for k, v in os.environ.items() if not k.startswith("CLAUDE") or k == "CLAUDE_CONFIG_DIR"}


def claude(*args, stdin=None, cwd=None, timeout=60):
    exe = shutil.which("claude")
    if not exe:
        sys.exit("pilot: claude is not on PATH")
    feed = {"input": stdin} if stdin is not None else {"stdin": subprocess.DEVNULL}
    return subprocess.run([exe, *args], **feed, cwd=cwd, env=claude_env(), capture_output=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


_agents_cache = {"at": 0.0, "rows": []}


def agents():
    """`claude agents --json --all`: every interactive and background session, completed ones included."""
    if time.time() - _agents_cache["at"] > 2:
        r = claude("agents", "--json", "--all")
        try:
            _agents_cache["rows"] = json.loads(r.stdout or "[]")
        except ValueError:
            _agents_cache["rows"] = []
        _agents_cache["at"] = time.time()
    return _agents_cache["rows"]


def session_record(pid):
    """<profile>/sessions/<pid>.json, which carries the Remote Control session id."""
    return read_json(PROFILE / "sessions" / f"{pid}.json", {}) if pid else {}


def job_state(job_id):
    """<profile>/jobs/<id>/state.json: Claude Code's own state for a background session, with a one-line
    `detail` of what it is doing or waiting on and, once a turn ends, its `output.result`."""
    d = read_json(PROFILE / "jobs" / job_id / "state.json", {}) if job_id else {}
    out = d.get("output") if isinstance(d.get("output"), dict) else {}
    return {"detail": d.get("detail"), "result": out.get("result")}


def transcript(session_id):
    hits = glob.glob(str(PROFILE / "projects" / "*" / f"{session_id}.jsonl")) if session_id else []
    return Path(hits[0]) if hits else None


def subagent_files(session_id):
    main = transcript(session_id)
    return sorted(main.parent.glob(f"{session_id}/subagents/agent-*.jsonl")) if main else []


def same_path(a, b):
    return os.path.normcase(os.path.normpath(a or "")) == os.path.normcase(os.path.normpath(b or ""))


def base_branch(root):
    """What a worker's worktree branches from: origin/HEAD, or in a repo with no remote, the branch its main
    checkout is on, which is what `claude --bg -w` takes then (measured on this repo, which has none)."""
    for ref in ("origin/HEAD", "HEAD"):
        r = subprocess.run(["git", "-C", root, "rev-parse", "--abbrev-ref", ref], capture_output=True, text=True)
        if r.returncode == 0 and r.stdout.strip() and r.stdout.strip() != "HEAD":
            return r.stdout.strip()
    return "HEAD"


# --------------------------------------------------------------------------- registry

def load(name):
    return read_json(WORKERS / f"{name}.json")


def save(w):
    write_json(WORKERS / f"{w['name']}.json", w)


def registered():
    return [json.loads(p.read_text(encoding="utf-8")) for p in sorted(WORKERS.glob("*.json"))]


def archived(limit=100):
    rows = [read_json(p) for p in ARCHIVE.glob("*.json")]
    return sorted((r for r in rows if r), key=lambda r: r.get("archivedAt") or 0, reverse=True)[:limit]


def need(name):
    w = load(name)
    if not w:
        sys.exit(f"pilot: no worker named {name}")
    return w


def sessions_of(w):
    """Every session a worker has run as, oldest first: the one `start` made, then one per `say`."""
    return w.get("sessions") or [{"id": w.get("id"), "sessionId": w.get("sessionId")}]


# --------------------------------------------------------------------------- flight plan

PLAN_TEMPLATE = """# Flight plan: {title}

Flight: {flight}

Objective: (one or two sentences: what this flight is for, and how you will know it landed)

Pilot session: {sid}

## Route

| Leg | Goal | After | Done when |
|---|---|---|---|

## Log

- {now} Plan opened.
"""


def flight_code(repo):
    """The next flight number for a repo: its name's initials, or its first three letters when it is one
    word, and a count kept in <state>/flights.json. drawboard-pdf-sdk flies DPS1, DPS2 and so on."""
    parts = [p for p in re.split(r"[-_. ]+", repo) if p]
    code = ("".join(p[0] for p in parts) if len(parts) > 1 else repo[:3]).upper()
    counts = read_json(FLIGHTS, {})
    counts[code] = counts.get(code, 0) + 1
    write_json(FLIGHTS, counts)
    return f"{code}{counts[code]}"


def plan_flight(sid, repo):
    """A plan's flight number, given it now if it has none: a plan opened before flight numbers existed gets
    one when its next leg takes off, on a `Flight:` line under the title."""
    p = plan_path(sid)
    if not p or not p.exists():
        return None
    t = p.read_text(encoding="utf-8")
    m = FLIGHT_RE.search(t)
    if m:
        return m.group(1)
    flight = flight_code(repo)
    title = re.search(r"^#\s.*$", t, re.M)
    at = title.end() if title else 0
    p.write_text(t[:at] + f"\n\nFlight: {flight}" + t[at:], encoding="utf-8")
    return flight


def pilot_session():
    """The session this command runs in, when the pilot runs it: Claude Code sets CLAUDE_CODE_SESSION_ID in
    every shell it starts. A flight plan belongs to that session, one to one."""
    return os.environ.get("CLAUDE_CODE_SESSION_ID")


def plan_path(sid):
    return PLANS / f"{sid}.md" if sid else None


def plan_log(sid, text):
    """Appends a line to a flight plan's Log, which the template keeps as the last section."""
    p = plan_path(sid)
    if not p or not p.exists():
        return
    t = p.read_text(encoding="utf-8")
    t += ("" if t.endswith("\n") else "\n") + ("" if "\n## Log" in t else "\n## Log\n\n")
    p.write_text(t + f"- {time.strftime('%Y-%m-%d %H:%M')} {text}\n", encoding="utf-8")


FLIGHT_RE = re.compile(r"^Flight:\s*(\S+)", re.M)
OBJECTIVE_RE = re.compile(r"^Objective:\s*(.+(?:\n(?!\s*\n|#|\|)[^\n]+)*)", re.M)   # it may wrap, up to a blank line
# The plan's list of what the pilot is waiting on the controller for, under any of these headings.
WAITING_RE = re.compile(r"(waiting on (the controller|you)|open questions|questions for the controller)\b", re.I)


def parse_plan(text):
    def section(name):
        m = re.search(rf"^##\s+{name}\s*$(.*?)(?=^##\s|\Z)", text, re.M | re.S)
        return m.group(1) if m else ""
    title = re.search(r"^#\s+(?:Flight plan:\s*)?(.+)$", text, re.M)
    objective = OBJECTIVE_RE.search(text)
    legs = []
    for line in section("Route").splitlines():
        if not line.strip().startswith("|"):
            continue
        row = line.strip()
        row = row[1:] if row.startswith("|") else row
        row = row[:-1] if row.endswith("|") and not row.endswith("\\|") else row
        cells = [c.strip().replace("\\|", "|") for c in re.split(r"(?<!\\)\|", row)]   # a `\|` stays in its cell
        if not cells or set("".join(cells)) <= set("-: ") or cells[0].lower() == "leg":
            continue
        cells += [""] * (4 - len(cells))
        name = re.sub(r"[`*]", "", cells[0]).strip()
        after = [a for a in (x.strip(" `*") for x in re.split(r"[,;\s]+", cells[2])) if a and a not in ("-", "none")]
        legs.append({"leg": name, "goal": cells[1], "after": after, "doneWhen": cells[3]})
    log = []
    for ln in section("Log").splitlines():
        if ln.startswith("- "):
            log.append(ln[2:].strip())
        elif log and ln[:1].isspace() and ln.strip():   # an entry wrapped onto the next line
            log[-1] += " " + ln.strip()
    return {"title": title.group(1).strip() if title else "untitled", "objective": " ".join(objective.group(1).split()) if objective else "",
            "flight": (FLIGHT_RE.search(text) or [None, None])[1], "legs": legs, "log": log[-40:], "notes": plan_notes(split_waiting(text)[1]), "waiting": split_waiting(text)[0]}


def split_waiting(text):
    """The plan's `## Waiting on the controller` section, and the rest of the plan without it. The section
    ends at the next heading, or where its list ends: a paragraph after the list, or the `Pilot session:`
    line, is the plan's own, wherever the pilot put the section."""
    waiting, rest, inside, listed, gap = [], [], False, False, False
    for line in text.splitlines():
        h = re.match(r"^(##|###)\s+(\S.*?)\s*$", line)
        if h:
            inside, listed, gap = h.group(1) == "##" and bool(WAITING_RE.match(h.group(2))), False, False
            if inside:
                continue
        elif inside:
            blank, item = not line.strip(), re.match(r"^([-*]|\d+[.)])\s", line)
            if not (line.startswith(("Pilot session:", "Flight:")) or (listed and gap and not blank and not item and not line[:1].isspace())):
                waiting.append(line)
                listed, gap = listed or bool(item), blank
                continue
            inside = False
        rest.append(line)
    return "\n".join(waiting).strip(), "\n".join(rest)


def plan_notes(text):
    """Everything the pilot wrote besides the title, objective, route table, log and what it waits on the
    controller for: what it found, what shapes the route. That is its reasoning; the controller sees it."""
    text = OBJECTIVE_RE.sub("", text, count=1)
    out, part = [], None
    for line in text.splitlines():
        h = re.match(r"^##\s+(\S.*?)\s*$", line)
        if h:
            part = h.group(1).lower()
            if part not in ("route", "log"):     # a section of the pilot's own
                out.append(line)
            continue
        if part == "log" or line.startswith("# ") or re.match(r"^(Pilot session|Flight):", line) or line.lstrip().startswith("|"):
            continue
        if part == "route" and line.strip() and "#### On the route" not in out:
            out.append("#### On the route")
        out.append(line)
    return re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip()


def plans_view(rows, workers, seen=None):
    """Flight plans whose pilot is live, or that a current worker flies a leg of, or touched this week. A live
    pilot comes with what it is doing (session_view), reusing `seen`, the pilots already looked at."""
    live = {r.get("sessionId"): r for r in rows if r.get("pid")}
    flown = {w.get("plan") for w in workers if w.get("plan")}
    seen = seen or {}
    out = []
    for p in sorted(PLANS.glob("*.md"), key=lambda p: p.stat().st_mtime, reverse=True):
        sid, mtime = p.stem, p.stat().st_mtime
        if sid not in live and sid not in flown and time.time() - mtime > 7 * 86400:
            continue
        out.append({"sessionId": sid, "path": str(p), "updated": mtime, "live": sid in live,
                    "pilot": (live.get(sid) or {}).get("name"),
                    "pilotState": (seen.get(sid) or session_view(live[sid])) if sid in live else None,
                    **parse_plan(p.read_text(encoding="utf-8"))})
    return out[:10]


# --------------------------------------------------------------------------- spend

WEIGHTS = {"input_tokens": 1.0, "cache_creation_input_tokens": 1.25, "cache_read_input_tokens": 0.1, "output_tokens": 5.0}


def local_day(ts):
    try:
        return datetime.datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone().date().isoformat()
    except (AttributeError, ValueError):
        return None


class Meter:
    """Weighted tokens of one transcript file, read incrementally: input + 1.25*cache_creation +
    0.1*cache_read + 5*output, the same price ratios Sam's agent_token_budget.sh uses. A message's usage is
    repeated on every content block it was streamed in, so it is counted once per message id; a copy made
    by `say` repeats the messages before it under the same ids, so they are counted once across files too."""

    def __init__(self, path):
        self.path, self.offset, self.entries = Path(path), 0, {}

    def read(self):
        try:
            with open(self.path, "rb") as f:
                f.seek(self.offset)
                chunk = f.read()
        except OSError:
            return self
        end = chunk.rfind(b"\n") + 1      # a trailing partial line is read again next time
        self.offset += end
        for line in chunk[:end].splitlines():
            if b'"usage"' not in line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            m = e.get("message") if isinstance(e, dict) else None
            if e.get("type") != "assistant" or not isinstance(m, dict) or not isinstance(m.get("usage"), dict):
                continue
            u = m["usage"]
            raw = [int(u.get(k) or 0) for k in WEIGHTS]
            self.entries[m.get("id") or e.get("uuid")] = (sum(w * n for w, n in zip(WEIGHTS.values(), raw)), raw,
                                                         local_day(e.get("timestamp")))
        return self


_meters = {}


def spend(session_ids):
    """Weighted tokens, raw counts and weighted tokens per local day of some sessions, their subagents
    included, every message counted once."""
    entries = {}
    files = 0
    for sid in session_ids:
        main = transcript(sid)
        if not main:
            continue
        for f in [main, *subagent_files(sid)]:
            m = _meters.get(str(f)) or _meters.setdefault(str(f), Meter(f))
            entries.update(m.read().entries)
            files += 1
    tokens = {k: sum(e[1][i] for e in entries.values()) for i, k in enumerate(WEIGHTS)}
    days = {}
    for weighted, _, day in entries.values():
        if day:
            days[day] = days.get(day, 0) + weighted
    return {"weighted": int(sum(e[0] for e in entries.values())), "tokens": tokens,
            "days": {d: int(v) for d, v in days.items()}, "files": files}


# --------------------------------------------------------------------------- transcripts

def flat(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(b.get("text", "") if isinstance(b, dict) else str(b) for b in content)
    return "" if content is None else json.dumps(content)


def brief_input(inp):
    if not isinstance(inp, dict):
        return ""
    for k in ("command", "file_path", "pattern", "description", "prompt", "url", "to", "skill"):
        if inp.get(k):
            return str(inp[k])[:300]
    return json.dumps(inp)[:300]


# How Claude Code words a call it refused, and who refused it (measured on this machine's transcripts).
DENIALS = [
    (re.compile(r"denied by the Claude Code auto mode classifier"), "auto mode"),
    (re.compile(r"^PreToolUse:\w+ hook error"), "a hook"),
    (re.compile(r"^<tool_use_error>Blocked:"), "Claude Code"),
    (re.compile(r"Permission to use .* has been denied|matches a deny rule", re.I), "a deny rule"),
    (re.compile(r"doesn't want to proceed with this tool use"), "a prompt nobody approved"),
]


def user_text(c, ev, queued=False):
    """A user turn's text: a message from another session, Claude Code's notice that a background job the
    session started has finished (a command, a subagent), or a prompt. A notice or message waiting for the
    session's turn to end arrives queued, as an attachment, but is the same thing."""
    if "<cross-session-message" in c:
        m = re.search(r'from-name="([^"]*)"', c)
        body = re.sub(r"<cross-session-message[^>]*>|</cross-session-message>", "", c)
        body = body.replace("Another Claude session sent a message:", "").strip()
        ev("message", sender=m.group(1) if m else "another session", text=body[:20000])
    elif "<task-notification>" in c:
        m = re.search(r"<summary>(.*?)</summary>", c, re.S)
        ev("notice", text=(m.group(1) if m else re.sub(r"<[^>]+>", " ", c)).strip()[:2000])
    else:
        ev("prompt", text=c[:20000], **({"queued": True} if queued else {}))


def events_of(e):
    """One transcript record as the events the session view shows."""
    out = []
    t, ts = e.get("type"), e.get("timestamp")
    msg = e.get("message") if isinstance(e.get("message"), dict) else {}
    c = msg.get("content")

    def ev(kind, **kw):
        out.append({"kind": kind, "at": ts, **kw})

    if t == "assistant" and (e.get("error") or e.get("isApiErrorMessage")):
        ev("error", text=f"{e.get('error') or 'API error'}: {flat(c)[:2000]}")
    elif t == "assistant" and isinstance(c, list):
        for b in c:
            k = b.get("type") if isinstance(b, dict) else None
            if k == "text" and b.get("text", "").strip():
                ev("said", text=b["text"][:20000])
            elif k == "thinking" and b.get("thinking", "").strip():
                ev("thinking", text=b["thinking"][:8000])
            elif k == "tool_use":
                inp = b.get("input")
                ev("tool", id=b.get("id"), tool=b.get("name"), summary=brief_input(inp),
                   input=(json.dumps(inp, indent=1, ensure_ascii=False) if inp is not None else "")[:12000])
    elif t == "user" and isinstance(c, str) and not e.get("isMeta"):
        user_text(c, ev)
    elif t == "user" and isinstance(c, list):
        tur = e.get("toolUseResult") if isinstance(e.get("toolUseResult"), dict) else {}
        for b in c:
            if not isinstance(b, dict):
                continue
            if b.get("type") == "tool_result":
                text = flat(b.get("content"))
                denied = next((who for rx, who in DENIALS if rx.search(text)), None) if b.get("is_error") else None
                ev("result", id=b.get("tool_use_id"), error=bool(b.get("is_error")), denied=denied,
                   agentId=tur.get("agentId"), text=text[:12000])
            elif b.get("type") == "text" and not e.get("isMeta") and b.get("text", "").strip():
                ev("prompt", text=b["text"][:20000])
    elif t == "attachment":
        a = e.get("attachment") or {}
        if a.get("type") == "queued_command" and (a.get("prompt") or a.get("content")):
            user_text(flat(a.get("prompt") or a.get("content")), ev, queued=True)
    elif t == "system":
        st = e.get("subtype")
        if st == "turn_duration":
            ev("turn", ms=e.get("durationMs"))
        elif st == "away_summary" and e.get("content"):
            ev("recap", text=flat(e.get("content"))[:4000])
        elif st == "compact_boundary":
            ev("compact")
    return out


def read_events(path, offset=0):
    """Events from a transcript after a byte offset, and the offset of the last complete line read."""
    try:
        with open(path, "rb") as f:
            f.seek(offset)
            chunk = f.read()
    except OSError:
        return [], offset
    end = chunk.rfind(b"\n") + 1
    out = []
    for line in chunk[:end].splitlines():
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if isinstance(e, dict):
            out += events_of(e)
    return out, offset + end


def tail_events(session_id, n=40, tail_bytes=512 * 1024):
    """A session's last n events, newest last, read from the end of its transcript."""
    path = transcript(session_id)
    if not path:
        return []
    size = path.stat().st_size
    start = max(0, size - tail_bytes)
    if start:
        with open(path, "rb") as f:          # start on a line boundary
            f.seek(start)
            start += len(f.readline())
    return read_events(path, start)[0][-n:]


def report(session_id):
    """The session's last assistant text: a finished worker's report."""
    said = [e for e in tail_events(session_id, n=400, tail_bytes=4 * 1024 * 1024) if e["kind"] == "said"]
    return said[-1]["text"] if said else ""


def head_word(text):
    m = re.match(r"^\W*(DONE|BLOCKED|FAILED)\b", text or "")
    return m.group(1) if m else None


def subagents(session_id):
    out = []
    for f in subagent_files(session_id):
        aid = f.stem[len("agent-"):]
        meta = read_json(f.with_name(f"agent-{aid}.meta.json"), {})
        m = _meters.get(str(f)) or _meters.setdefault(str(f), Meter(f))
        out.append({"id": aid, "description": meta.get("description"), "type": meta.get("agentType"),
                    "weighted": int(sum(e[0] for e in m.read().entries.values())), "lastActivity": f.stat().st_mtime})
    return out


# --------------------------------------------------------------------------- status

def checklist(session_ids):
    """A session's checklist, the task list it keeps with TaskCreate and TaskUpdate, which Claude Code
    stores as <profile>/tasks/<session>/<n>.json: from the newest of the sessions given that has one. Each
    item: id, subject, status (pending, in_progress, completed) and activeForm, how it reads in progress."""
    for sid in reversed([s for s in session_ids if s]):
        items = []
        for f in (PROFILE / "tasks" / sid).glob("*.json"):
            t = read_json(f)
            if isinstance(t, dict) and t.get("subject") and t.get("status") != "deleted":
                items.append({k: t.get(k) for k in ("id", "subject", "status", "activeForm")})
        if items:
            return sorted(items, key=lambda x: int(x["id"]) if str(x.get("id")).isdigit() else 0)
    return []


def open_items(items):
    return [i for i in items if i.get("status") != "completed"]


def doing_now(ev):
    """The last tool call or reply among a session's events: what it is doing, or did last."""
    return next(({k: (str(v)[:300] if k == "text" else v) for k, v in e.items() if k != "input"}
                 for e in reversed(ev) if e["kind"] in ("tool", "said")), None)


def worker_status(w, rows=None):
    rows = agents() if rows is None else rows
    row = next((r for r in rows if r.get("id") == w["id"] or r.get("sessionId") == w.get("sessionId")), None)
    if row and not w.get("sessionId") and row.get("sessionId"):
        w["sessionId"] = row["sessionId"]
        save(w)
    sid = w.get("sessionId")
    path = transcript(sid)
    sp = spend([s["sessionId"] for s in sessions_of(w) if s.get("sessionId")])
    last = None
    if path:
        last = max([path.stat().st_mtime] + [p.stat().st_mtime for p in subagent_files(sid)])
    ev = tail_events(sid, n=60) if sid else []
    now = doing_now(ev)
    said = [e for e in ev if e["kind"] == "said"]
    tail = [e for e in ev if e["kind"] in ("said", "error")]
    rec = session_record(row.get("pid")) if row and row.get("pid") else {}
    state = (row or {}).get("state") or ("gone" if not row else (row.get("status") or "?"))
    job = job_state(w["id"])
    return {
        **{k: w.get(k) for k in ("name", "id", "sessionId", "repo", "worktree", "branch", "base", "model", "effort",
                                 "budget", "started", "pilot", "after", "plan")}, "agent": agent_name(w),
        "sessions": len(sessions_of(w)),
        "state": state, "busy": (row or {}).get("status") == "busy", "pid": (row or {}).get("pid"),
        "detail": job["detail"], "reportHead": head_word(said[-1]["text"]) if said else None,
        "lastError": tail[-1]["text"] if tail and tail[-1]["kind"] == "error" else None,
        "spend": sp, "overBudget": bool(w.get("budget")) and sp["weighted"] >= w["budget"],
        "lastActivity": last, "now": now, "rev": path.stat().st_size if path else 0,
        "checklist": checklist([s.get("sessionId") for s in sessions_of(w)]),
        "remote": f"https://claude.ai/code/{rec['bridgeSessionId']}" if rec.get("bridgeSessionId") else None,
    }


def pilots(rows=None):
    """Live sessions whose working directory is a configured repo's own checkout: the pilot, and anything
    else you have open there."""
    rows = agents() if rows is None else rows
    roots = list(config()["repos"].values())
    out = []
    for r in rows:
        if r.get("pid") and any(same_path(r.get("cwd"), root) for root in roots) and r.get("state") != "done":
            path = transcript(r["sessionId"])
            out.append({"name": r.get("name"), "id": r.get("id"), "sessionId": r.get("sessionId"), "kind": r.get("kind"),
                        "spend": spend([r["sessionId"]]), "rev": path.stat().st_size if path else 0,
                        "lastActivity": path.stat().st_mtime if path else None, **session_view(r)})
    return out


def pending_question(ev):
    """An AskUserQuestion that has no answer yet, if the transcript already holds the call. Claude Code
    writes it there before the answer at least some of the time (seen once without, once with), so this
    covers a session the question hook missed, such as one started before the hook existed."""
    answered = {e.get("id") for e in ev if e["kind"] == "result"}
    for e in reversed(ev):
        if e["kind"] == "tool" and e.get("tool") == "AskUserQuestion" and e.get("id") not in answered:
            try:
                at = datetime.datetime.fromisoformat(str(e.get("at")).replace("Z", "+00:00")).timestamp()
                return {"at": at, "questions": json.loads(e.get("input") or "{}").get("questions") or []}
            except ValueError:
                return None
    return None


def session_view(r):
    """What a live session is doing, from its `claude agents` row: Claude Code's own status (busy, idle, or
    waiting on you, for what and since when), its last tool call or reply, and the question it is asking.
    Claude Code writes a pending AskUserQuestion to the transcript only once it is answered, so the question
    comes from the pilot plugin's hook (cmd_hook_ask), and only while the session still waits on it."""
    sid, rec = r.get("sessionId"), session_record(r.get("pid"))
    status = r.get("status") or rec.get("status")
    since = (rec.get("statusUpdatedAt") or 0) / 1000 or None
    ask = read_json(ASKS / f"{sid}.json") if status == "waiting" and sid else None
    if ask and since and ask.get("at", 0) < since - 60:       # asked before this wait began: a cancelled question
        ask = None
    ev = tail_events(sid, n=60) if sid else []
    if status == "waiting" and not ask:
        ask = pending_question(ev)
    now, said = doing_now(ev), next((e for e in reversed(ev) if e["kind"] == "said"), None)
    return {"said": {"text": said["text"][:8000], "at": said["at"]} if said else None, "checklist": checklist([sid]), "status": status, "busy": status == "busy", "waitingFor": r.get("waitingFor") or rec.get("waitingFor"),
            "since": since, "now": now, "ask": ask,
            "remote": f"https://claude.ai/code/{rec['bridgeSessionId']}" if rec.get("bridgeSessionId") else None}


def limits():
    """The account's 5-hour and 7-day windows, if a status line writes Sam's rate-limit cache."""
    d = read_json(PROFILE / "rate-limits.json")
    return {"windows": d.get("rate_limits"), "writtenAt": d.get("written_at")} if d else None


def snapshot():
    rows = agents()
    history = []
    for a in archived(limit=40):
        h = {k: a.get(k) for k in ("name", "repo", "branch", "base", "worktree", "model", "effort", "started", "archivedAt",
                                   "pilot", "after", "sessionId", "sessions")}
        h["final"] = {**(a.get("final") or {}), "report": ((a.get("final") or {}).get("report") or "")[:3000]}
        history.append(h)
    workers = [worker_status(w, rows) for w in registered()]
    ps = pilots(rows)
    for p in ps:
        p["flying"] = [w["name"] for w in workers if w.get("plan") == p["sessionId"] and w["state"] in ("working", "blocked")]
    return {"workers": workers, "pilots": ps, "limits": limits(), "history": history,
            "plans": plans_view(rows, workers, {p["sessionId"]: p for p in ps})}


def fingerprint(session_ids):
    """Size and mtime of every file whose change can change the dashboard, in two parts. The first is what
    `claude agents` reports from: Claude Code's live-session records, its background-job states and daemon
    roster, plus the worker registry and archive. The second is the rate-limit cache and the transcripts of
    the sessions on show, which change spend and activity but no session's state. Statting them every second
    costs nothing; asking claude does, so it is asked only when the first part moves."""
    def stats(files):
        out = []
        for f in files:
            try:
                st = f.stat()
                out.append((str(f), st.st_mtime_ns, st.st_size))
            except OSError:
                pass
        return tuple(out)
    sessions = [*PROFILE.glob("sessions/*.json"), *PROFILE.glob("jobs/*/state.json"), PROFILE / "daemon" / "roster.json",
                *WORKERS.glob("*.json"), *ARCHIVE.glob("*.json")]
    content = [PROFILE / "rate-limits.json", SUMMARIES, *PLANS.glob("*.md"), *ASKS.glob("*.json")]
    for sid in session_ids:
        main = transcript(sid)
        if main:
            content += [main, *subagent_files(sid)]
        content += PROFILE.glob(f"tasks/{sid}/*.json")
    return stats(sessions), stats(content)


# --------------------------------------------------------------------------- notifications

class Notices:
    """The notices that pile up while the dashboard runs, kept in <state>/notifications.json: a worker
    waiting on you, a turn ended (DONE, BLOCKED or FAILED), a worker over budget, quiet for 30 minutes, or
    dead on an API, login or limit error, and a pilot waiting on you. The first look at a session records it
    without a notice."""

    def __init__(self):
        d = read_json(NOTICES, {})
        self.items, self.seen, self.lock = d.get("items", []), d.get("seen"), threading.Lock()

    def save(self):
        write_json(NOTICES, {"items": self.items[-300:], "seen": self.seen})

    def update(self, snap):
        first, seen, now, new = self.seen is None, self.seen or {}, time.time(), []

        def add(kind, w, text, href=None):
            new.append({"id": f"{int(now * 1000)}-{len(new)}", "at": now, "kind": kind, "worker": w["name"],
                        "sessionId": w.get("sessionId"), "text": text, "read": False, "href": href})
        for w in snap["workers"]:
            prev, st = seen.get(w["name"]), w["state"]
            quiet = st == "working" and bool(w["lastActivity"]) and now - w["lastActivity"] > QUIET_SECONDS
            if prev is not None:
                if st == "blocked" and prev.get("state") != "blocked":
                    add("waiting", w, f"{w['name']} is waiting on you" + (f": {w['detail']}" if w.get("detail") else ""))
                if st == "done" and prev.get("state") not in ("done", None) and prev.get("sid") == w.get("sessionId"):
                    head = w.get("reportHead")
                    kind = {"DONE": "done", "BLOCKED": "waiting", "FAILED": "failed"}.get(head, "turn")
                    add(kind, w, f"{w['name']}: {head or 'turn ended'}" + (f" ({w['detail']})" if w.get("detail") else ""))
                if w["overBudget"] and not prev.get("over"):
                    add("budget", w, f"{w['name']} reached its budget and was told to report")
                if quiet and not prev.get("quiet"):
                    add("quiet", w, f"{w['name']} has written nothing for 30 minutes")
                if w.get("lastError") and w["lastError"] != prev.get("err"):
                    add("died", w, f"{w['name']} stopped on an error: {w['lastError'][:200]}")
                if st == "gone" and prev.get("state") not in ("gone", "done", None):
                    add("died", w, f"{w['name']} is gone")
            seen[w["name"]] = {"state": st, "over": w["overBudget"], "quiet": quiet, "err": w.get("lastError"),
                               "sid": w.get("sessionId")}
        for p in snap["pilots"]:
            key = "pilot:" + p["sessionId"]
            if seen.get(key) is not None and p["status"] == "waiting" and seen[key].get("state") != "waiting":
                q = next(iter((p.get("ask") or {}).get("questions") or []), {})
                add("waiting", p, f"pilot {p['name']} is waiting on you: {q.get('question') or p.get('waitingFor') or 'input'}",
                    href=f"#/s/{p['sessionId']}")
            elif seen.get(key, {}).get("state") == "busy" and p["status"] == "idle" and not p.get("flying"):
                first = next((ln.strip(" #*>-`") for ln in ((p.get("said") or {}).get("text") or "").splitlines() if ln.strip()), "")
                add("turn", p, f"pilot {p['name']} ended its turn, nothing in the air" + (f": {first[:200]}" if first else ""),
                    href=f"#/s/{p['sessionId']}")
            seen[key] = {"state": p["status"]}
        for pl in snap["plans"]:
            key, items = "plan:" + pl["sessionId"], pl.get("waiting") or ""
            if seen.get(key) is not None and items and items != seen[key].get("waiting"):
                add("waiting", {"name": pl.get("pilot") or pl["title"], "sessionId": pl["sessionId"]},
                    f"{pl['title']}: the pilot is waiting on you", href=f"#/s/{pl['sessionId']}")
            seen[key] = {"waiting": items}
        with self.lock:
            self.seen = seen
            if new or first:
                self.items += new
                self.save()
        return new

    def view(self):
        with self.lock:
            return {"unread": sum(1 for i in self.items if not i["read"]), "items": self.items[-80:][::-1]}

    def read(self, ids=None):
        with self.lock:
            for i in self.items:
                if ids is None or i["id"] in ids:
                    i["read"] = True
            self.save()


# --------------------------------------------------------------------------- summaries

SUMMARY_PROMPT = """You summarise background coding workers for the person who runs them. Below are records
about {what}. Answer in exactly this format and nothing else:

## Needs you
- One bullet per thing that stays stuck unless the person acts: a question only they can answer, a
  permission prompt, a login, a budget checkpoint, a worker that failed or died. If there is nothing,
  write the single word: Nothing.

## Where it is
At most {words} words of plain prose: what has been done, what is happening now, what is left. Quote
commit hashes, test names and results only from the records; never invent them.

Records:
"""


def worker_records(w, events=50):
    s = worker_status(w)
    lines = [f"Worker {w['name']} in repo {w['repo']}, branch {w['branch']} from {w.get('base') or 'origin/HEAD'}.",
             f"State: {s['state']}" + (f" ({s['detail']})" if s.get("detail") else "") +
             f". Spend {s['spend']['weighted']:,} of {w.get('budget') or 0:,} weighted tokens.",
             f"Started {time.strftime('%Y-%m-%d %H:%M', time.localtime(w.get('started') or 0))}; last activity "
             f"{time.strftime('%H:%M', time.localtime(s['lastActivity'])) if s['lastActivity'] else 'never'}."]
    brief = (BRIEFS / f"{w['name']}.md")
    if brief.exists():
        lines += ["", "Brief:", brief.read_text(encoding="utf-8").split("# Brief\n", 1)[-1].strip()[:2500]]
    if Path(w["worktree"]).exists():
        r = subprocess.run(["git", "-C", w["worktree"], "log", "--oneline", "-15", f"{w.get('base') or 'origin/HEAD'}..HEAD"],
                           capture_output=True, text=True)
        lines += ["", "Commits on its branch:", r.stdout.strip() or "(none)"]
    lines += ["", f"Last {events} events:"]
    for e in tail_events(w.get("sessionId"), n=events):
        what = e.get("tool") or e["kind"]
        lines.append(f"- {e.get('at', '')[11:19]} {what}: {(e.get('summary') or e.get('text') or '')[:300]}".replace("\n", " "))
    rep = report(w.get("sessionId")) if w.get("sessionId") else ""
    if rep:
        lines += ["", "Its last message:", rep[:3000]]
    return "\n".join(lines)


def summarize(name=None):
    """One `claude -p` on Haiku with no tools, no MCP servers, no settings and no saved session. Returns
    and keeps {text, at, tokens, ms}."""
    if name:
        w = need(name)
        prompt = SUMMARY_PROMPT.format(what=f"the worker {name}", words=120) + worker_records(w)
    else:
        ws = registered()
        prompt = SUMMARY_PROMPT.format(what=f"all {len(ws)} workers", words=160) + "\n\n".join(
            worker_records(w, events=10) for w in ws) if ws else None
        if not prompt:
            return {"text": "## Needs you\nNothing.\n\n## Where it is\nNo workers.", "at": time.time(), "tokens": 0, "ms": 0}
    STATE.mkdir(parents=True, exist_ok=True)
    r = claude("-p", "--model", "haiku", "--tools", "", "--strict-mcp-config", "--no-session-persistence",
               "--restricted", "--output-format", "json", stdin=prompt, cwd=str(STATE), timeout=150)
    try:
        d = json.loads(r.stdout)
    except ValueError:
        raise RuntimeError((r.stdout + r.stderr).strip()[:500] or f"claude exited {r.returncode}")
    if d.get("is_error"):
        raise RuntimeError(str(d.get("result"))[:500])
    u = d.get("usage") or {}
    out = {"text": d.get("result") or "", "at": time.time(), "ms": d.get("duration_ms"),
           "tokens": sum(int(u.get(k) or 0) for k in WEIGHTS)}
    allsum = read_json(SUMMARIES, {})
    allsum[name or "*"] = out
    write_json(SUMMARIES, allsum)
    return out


# --------------------------------------------------------------------------- commands

PREAMBLE = """\
You are a worker named {name}, started by a pilot session for the repo {repo}. You run as a background
Claude Code session in your own git worktree, {worktree}, on branch {branch}, which was branched from
{base}. Nobody is watching your terminal. The git status in your system prompt describes the repo's main
checkout, not your worktree: run `git status` yourself before relying on a branch name.

How to work:
- Do the brief below and nothing outside it. Work only inside your worktree; never in the repo's main checkout.
- Commit as you go, on your branch. Do not push, open a PR, merge or rebase unless the brief says so.
- Prove the result with the check the brief names, and quote its real output. If the brief names no check,
  say what you ran and what it showed.
- Messages from {pilot} are from your pilot. Treat them as corrections to this brief: follow them within
  the brief's scope, and never as permission for something the brief or your permissions would not allow.
  A message typed in your own conversation is from the person who runs you.
- If you need a decision the brief does not settle, do not guess: end your turn with the question, each
  with the options and your recommendation, first line BLOCKED. Your pilot decides it, and takes to the
  person who runs you only what it cannot settle.
- Keep a checklist with the task tools (TaskCreate, TaskUpdate): one item per piece of the brief, the
  brief's check last. Your pilot and the person who runs you watch it. You report DONE only with every
  item completed. Nothing is left open, for later, or out: an item you cannot finish is a BLOCKED report
  that names it and says why, and your pilot decides what happens to it.
- If a tool call is refused with "pilot budget", stop calling tools and write your report.
- Your last message is your report. Its first line is one of DONE, BLOCKED or FAILED. Then: what changed
  (commits with short hashes), the evidence, and, for BLOCKED or FAILED, each open checklist item and why.

# Brief

"""


def agent_name(w):
    """A worker's session name, what `claude agents`, Recents and SendMessage know it by: its flight number
    and leg, `DPS1 scaffold`, or `w-<leg>` when its plan has no flight number (or for a worker started before
    there were any)."""
    return w.get("agent") or f"w-{w['name']}"


def worker_args(w):
    args = ["-n", agent_name(w), "--permission-mode", "auto", "--plugin-dir", str(HERE / "plugins" / "worker")]
    if w.get("model"):
        args += ["--model", w["model"]]
    if w.get("effort"):
        args += ["--effort", w["effort"]]
    return args


def backgrounded(r):
    m = re.search(r"backgrounded\s*\S\s*([0-9a-f]{8})", r.stdout)
    if r.returncode != 0 or not m:
        sys.exit(f"pilot: claude --bg failed ({r.returncode}): {(r.stdout + r.stderr).strip()[:800]}")
    return m.group(1)


def session_of(job_id):
    for _ in range(20):                       # the session id lands in `claude agents` within a second or two
        _agents_cache["at"] = 0
        row = next((x for x in agents() if x.get("id") == job_id), None)
        if row and row.get("sessionId"):
            return row["sessionId"]
        time.sleep(0.5)
    return None


def cmd_start(a):
    cfg = config()
    if not NAME_RE.match(a.name):
        sys.exit("pilot: a worker name is lowercase letters, digits and hyphens, at most 30")
    if load(a.name):
        sys.exit(f"pilot: {a.name} exists; `pilot.py rm {a.name}` first, or pick another name")
    for dep in a.after or []:
        if not load(dep):
            sys.exit(f"pilot: --after {dep}: no worker of that name")
    # The pilot runs in its repo's main checkout, so that is where its workers start. Falling back to the first
    # configured repo would quietly fly a leg in somebody else's repo.
    top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True).stdout.strip()
    repo = a.repo or next((k for k, v in cfg["repos"].items() if same_path(v, top)), None)
    if not repo:
        sys.exit(f"pilot: {top or os.getcwd()} is not in {CONFIG} (it knows {', '.join(cfg['repos']) or 'none'}); add it, or pass --repo")
    root = cfg["repos"].get(repo)
    if not root or not Path(root, ".git").exists():
        sys.exit(f"pilot: unknown repo {repo}; {CONFIG} knows {', '.join(cfg['repos']) or 'none'}")
    exclude = Path(root, ".git", "info", "exclude")    # keep the workers' worktrees out of the user's `git status`
    if Path(root, ".git").is_dir() and ".claude/worktrees/" not in (exclude.read_text(encoding="utf-8") if exclude.exists() else ""):
        exclude.parent.mkdir(parents=True, exist_ok=True)
        with exclude.open("a", encoding="utf-8") as f:
            f.write("\n.claude/worktrees/\n")
    brief = sys.stdin.read() if a.brief == "-" else Path(a.brief).read_text(encoding="utf-8")
    if not brief.strip():
        sys.exit("pilot: the brief is empty")
    wt = f"w-{a.name}"
    w = {"name": a.name, "repo": repo, "worktree": str(Path(root, ".claude", "worktrees", wt)), "branch": f"worktree-{wt}",
         "base": base_branch(root), "model": a.model or cfg.get("model"), "effort": a.effort or cfg.get("effort"),
         "budget": a.budget or cfg.get("budget"), "started": time.time(), "pilot": a.pilot, "after": a.after or [],
         "plan": pilot_session()}
    flight = plan_flight(w["plan"], repo) if w["plan"] else None
    w["agent"] = f"{flight} {a.name}" if flight else f"w-{a.name}"
    prompt = PREAMBLE.format(pilot=f'the session named "{a.pilot}"' if a.pilot else "other Claude sessions on this machine",
                             name=w["name"], repo=repo, worktree=w["worktree"], branch=w["branch"], base=w["base"]) + brief
    w["id"] = backgrounded(claude("--bg", "-w", wt, *worker_args(w), stdin=prompt, cwd=root, timeout=120))
    BRIEFS.mkdir(parents=True, exist_ok=True)
    (BRIEFS / f"{a.name}.md").write_text(prompt, encoding="utf-8")
    save(w)
    w["sessionId"] = session_of(w["id"])
    w["sessions"] = [{"id": w["id"], "sessionId": w["sessionId"], "at": w["started"]}]
    save(w)
    plan_log(w["plan"], f"Leg {a.name} took off: worker {w['id']} on {w['branch']}" + (f", after {', '.join(w['after'])}" if w["after"] else ""))
    print(f"started {a.name}: {w['id']} in {w['worktree']} on {w['branch']} from {w['base']}")
    print(f"  message it:  SendMessage to \"{w['agent']}\"   |   you: pilot.py tell \"{w['agent']}\" -m ...  or  claude attach {w['id']}")


def say(name, text):
    """Your message to a worker as a user turn. Its session is stopped and continued with the message, which
    Claude Code does in a new session id holding the whole conversation; the worker's record moves there.
    A worker mid-turn, or waiting on a permission prompt or a question, is refused: stopping it would cut
    the turn off. Answer that one with `claude attach` or Remote Control."""
    w = need(name)
    if not text.strip():
        raise ValueError("the message is empty")
    _agents_cache["at"] = 0
    row = next((r for r in agents() if r.get("id") == w["id"]), None)
    if row and row.get("pid") and row.get("state") in ("working", "blocked"):
        raise ValueError(f"{name} is {row['state']}; a message would cut its turn off. "
                         f"Use `claude attach {w['id']}` or its Remote Control link.")
    if not Path(w["worktree"]).exists():
        raise ValueError(f"{name}'s worktree is gone")
    if row and row.get("pid"):
        claude("stop", w["id"])
        for _ in range(40):
            _agents_cache["at"] = 0
            row = next((r for r in agents() if r.get("id") == w["id"]), None)
            if not row or not row.get("pid"):
                break
            time.sleep(0.5)
    job = backgrounded(claude("--bg", "--resume", w["sessionId"], *worker_args(w), stdin=text, cwd=w["worktree"], timeout=120))
    sid = session_of(job)
    w["sessions"] = sessions_of(w) + [{"id": job, "sessionId": sid, "at": time.time()}]
    w["id"], w["sessionId"] = job, sid
    save(w)
    lines = text.strip().splitlines()
    more = "..." if len(lines) > 1 or len(lines[0]) > 140 else ""
    plan_log(w.get("plan"), f"Controller to {name}: {lines[0][:140]}{more}")
    return w


RELAY = "pilot-dashboard"      # the name the relay sends as

RELAY_PROMPT = """You relay messages for the person who runs these sessions. For each recipient below, call the
SendMessage tool once, with `to` set to the recipient's name exactly as written and `message` set to the
text between the two {mark} lines exactly as written: change nothing, add nothing, keep every line. Do
nothing else. Then answer with one line per recipient: its name, a colon, then "delivered", or "failed:"
and what the tool said.

Recipients:
{names}

{mark}
{text}
{mark}
"""


def intercom(names, text):
    """Your words to live sessions through Claude Code's own intercom (SendMessage), which only a Claude
    session can use, so one Haiku session allowed that tool alone sends them as `pilot-dashboard`: about 3 s
    and a tenth of a cent, measured. A message reaches a session at its next step, mid-turn included,
    without stopping or copying it, unlike `say`, and it reaches pilots too."""
    names = [n.strip() for n in names if n and n.strip()]
    if not text.strip():
        raise ValueError("the message is empty")
    if not names:
        raise ValueError("no recipients")
    mark = f"======== {os.urandom(4).hex()} ========"
    r = claude("-p", "--model", "haiku", "--tools", "SendMessage", "--allowedTools", "SendMessage", "--strict-mcp-config",
               "--no-session-persistence", "--name", RELAY, "--output-format", "json",
               stdin=RELAY_PROMPT.format(mark=mark, names="\n".join(f"- {n}" for n in names), text=text.strip()),
               cwd=str(STATE), timeout=180)
    try:
        out = json.loads(r.stdout).get("result") or ""
    except ValueError:
        raise ValueError(f"the relay failed ({r.returncode}): {(r.stdout + r.stderr).strip()[:400]}")
    lines = text.strip().splitlines()
    first = lines[0][:140] + ("..." if len(lines) > 1 or len(lines[0]) > 140 else "")
    rows = {x.get("name"): x for x in agents()}
    for n in names:
        w = next((x for x in registered() if agent_name(x) == n), None)
        plan = w.get("plan") if w else (rows.get(n) or {}).get("sessionId")
        plan_log(plan, f"Controller to {n} (intercom): {first}")
    return out


def set_budget(name, tokens):
    w = need(name)
    w["budget"] = tokens
    save(w)
    plan_log(w.get("plan"), f"Budget for {name} set to {tokens:,} weighted tokens")
    return w


def stop(name):
    w = need(name)
    r = claude("stop", w["id"])
    if r.returncode == 0:
        plan_log(w.get("plan"), f"Leg {name} stopped")
    return r


def cmd_tell(a):
    text = sys.stdin.read() if a.message == "-" else a.message
    try:
        print(intercom(a.names, text))
    except (ValueError, subprocess.TimeoutExpired) as e:
        sys.exit(f"pilot: {e}")


def cmd_say(a):
    text = sys.stdin.read() if a.text == ["-"] else " ".join(a.text)
    try:
        w = say(a.name, text)
    except ValueError as e:
        sys.exit(f"pilot: {e}")
    print(f"{a.name}: message delivered as a user turn; it continues as {w['id']}")


def cmd_list(a):
    rows = agents()
    ws = [worker_status(w, rows) for w in registered()]
    if a.json:
        print(json.dumps({"workers": ws, "pilots": pilots(rows), "limits": limits()}, indent=1))
        return
    if not ws:
        print("no workers")
    for s in ws:
        b = f"/{s['budget'] / 1e6:.0f}M" if s["budget"] else ""
        flag = "  OVER BUDGET" if s["overBudget"] else ""
        age = f"{(time.time() - s['lastActivity']) / 60:.0f}m ago" if s["lastActivity"] else "-"
        doing = (s["now"] or {}).get("tool") or ("said" if s["now"] else "")
        print(f"{s['name']:<20} {s['id']:<9} {s['state']:<9} {s['spend']['weighted'] / 1e6:6.2f}M{b:<5} last {age:<8} {doing}{flag}")


def cmd_result(a):
    """The worker's report, then its checklist. An item still open means the leg has not landed, whatever
    the report's first line says."""
    w = need(a.name)
    print(report(w["sessionId"]) if w.get("sessionId") else "(no session yet)")
    items = checklist([s.get("sessionId") for s in sessions_of(w)])
    if items:
        left = open_items(items)
        print(f"\n--- checklist: {len(items) - len(left)} of {len(items)} completed" + (f"; OPEN: {len(left)}, so it has not landed" if left else ""))
        for i in items:
            print(f"  [{'x' if i['status'] == 'completed' else '>' if i['status'] == 'in_progress' else ' '}] {i['subject']}")


def cmd_budget(a):
    set_budget(a.name, a.tokens)
    print(f"{a.name}: budget {a.tokens:,} weighted tokens")


def cmd_plan(a):
    """Prints the path of this pilot session's flight plan, creating it from the template the first time."""
    sid = a.session or pilot_session()
    if not sid:
        sys.exit("pilot: run this from the pilot session (it reads CLAUDE_CODE_SESSION_ID), or pass --session")
    p = plan_path(sid)
    if not p.exists():
        PLANS.mkdir(parents=True, exist_ok=True)
        top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True).stdout.strip()
        repo = next((k for k, v in config()["repos"].items() if same_path(v, top)), Path(top or os.getcwd()).name)
        p.write_text(PLAN_TEMPLATE.format(title=a.title or "untitled", flight=flight_code(repo), sid=sid,
                                          now=time.strftime("%Y-%m-%d %H:%M")), encoding="utf-8")
    print(p)


def cmd_summary(a):
    try:
        print(summarize(a.name)["text"])
    except RuntimeError as e:
        sys.exit(f"pilot: summary failed: {e}")


def cmd_stop(a):
    r = stop(a.name)
    print((r.stdout + r.stderr).strip())


def cmd_rm(a):
    """Removes every session the worker ran as, the copies `say` made first and the one that made the
    worktree last, since only that one owns it. The record is kept in the archive, with its report."""
    w = need(a.name)
    s = worker_status(w)
    rep = report(w["sessionId"]) if w.get("sessionId") else ""
    commits = ""
    if Path(w["worktree"]).exists():
        commits = subprocess.run(["git", "-C", w["worktree"], "log", "--oneline", "-30", f"{w.get('base') or 'origin/HEAD'}..HEAD"],
                                 capture_output=True, text=True).stdout.strip()
    ok = True
    for sess in reversed(sessions_of(w)):
        r = claude("rm", sess["id"])
        print((r.stdout + r.stderr).strip())
        ok = ok and (r.returncode == 0 or "no such" in (r.stdout + r.stderr).lower())
    if not ok:
        sys.exit("pilot: claude rm failed; the worker's record is kept")
    w["archivedAt"] = time.time()
    w["final"] = {"state": s["state"], "detail": s.get("detail"), "spend": s["spend"], "reportHead": s.get("reportHead"),
                  "report": rep[:6000], "commits": commits, "checklist": checklist([x.get("sessionId") for x in sessions_of(w)])}
    write_json(ARCHIVE / f"{a.name}-{int(w['archivedAt'])}.json", w)
    (WORKERS / f"{a.name}.json").unlink(missing_ok=True)
    plan_log(w.get("plan"), f"Leg {a.name} removed ({s.get('reportHead') or s['state']}, {s['spend']['weighted']:,} weighted tokens)")


def cmd_hook_budget(_a):
    """PreToolUse, every tool, in the worker plugin. A session pilot.py started is refused every tool but
    SendMessage once its weighted tokens, subagents included, reach its budget: a checkpoint, not a kill. It
    writes its report and the pilot decides. Any other session passes. An error passes too, since a broken
    guard must not stop a worker, but it is logged to <state>/hook-errors.log, never swallowed."""
    try:
        # Bytes, then UTF-8: a piped stdin on Windows decodes as the ANSI code page otherwise.
        p = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        sid = p.get("session_id") or ""
        w = next((x for x in registered() if any(s.get("sessionId") == sid or (s.get("id") and sid.startswith(s["id"]))
                                                 for s in sessions_of(x))), None)
        if not w or not w.get("budget") or p.get("tool_name") == "SendMessage":
            return
        METERS.mkdir(parents=True, exist_ok=True)
        cache = METERS / f"{sid}.json"
        saved = read_json(cache, {})
        main = Path(p.get("transcript_path") or transcript(sid) or "")
        files = [main, *sorted(main.parent.glob(f"{sid}/subagents/agent-*.jsonl"))]
        entries = {}
        for f in files:
            m = Meter(f)
            s = saved.get(str(f))
            if s:
                m.offset, m.entries = s["offset"], {k: tuple(v) for k, v in s["entries"].items()}
            m.read()
            saved[str(f)] = {"offset": m.offset, "entries": m.entries}
            entries.update(m.entries)
        write_json(cache, saved)
        total = int(sum(e[0] for e in entries.values()))
        # The transcript gets an assistant message only after its tool calls are dispatched, so this total
        # lags by the message in flight: a budget can be overshot by one message, never more.
        if total < w["budget"]:
            return
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse", "permissionDecision": "deny",
            "permissionDecisionReason": (
                f"pilot budget: this worker has spent {total:,} of its {w['budget']:,} weighted tokens. This is a "
                "checkpoint, not a failure. Stop calling tools. Write your report as your final message: where you "
                "got to, what remains, and what you think you still need. The pilot can raise the budget and resume you.")}}))
    except Exception:
        hook_error()


def hook_error():
    STATE.mkdir(parents=True, exist_ok=True)
    with open(STATE / "hook-errors.log", "a", encoding="utf-8") as f:
        f.write(f"--- {time.strftime('%Y-%m-%dT%H:%M:%S')}\n{traceback.format_exc()}")


def cmd_hook_ask(_a):
    """PreToolUse and PostToolUse on AskUserQuestion, in the pilot plugin. Claude Code writes a question to
    the transcript only once it is answered, so while the pilot waits on the controller nothing the dashboard
    reads says what for. Before it is asked, the questions go to <state>/asks/<session>.json, which the plan
    card shows while the pilot waits, and to the plan's log; once answered, the file goes and the answers go
    to the log, so the plan keeps every decision the controller made. It never refuses anything."""
    try:
        p = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        sid = p.get("session_id")
        if not sid:
            return
        one = lambda s: " ".join(str(s or "").split())
        f = ASKS / f"{sid}.json"
        if p.get("hook_event_name") == "PreToolUse":
            qs = (p.get("tool_input") or {}).get("questions") or []
            write_json(f, {"at": time.time(), "questions": qs})
            for q in qs:
                plan_log(sid, f"Pilot asks the controller ({one(q.get('header'))}): {one(q.get('question'))}")
            return
        f.unlink(missing_ok=True)
        r = p.get("tool_response") if isinstance(p.get("tool_response"), dict) else {}
        headers = {q.get("question"): q.get("header") for q in (p.get("tool_input") or {}).get("questions") or []}
        notes = r.get("annotations") if isinstance(r.get("annotations"), dict) else {}
        for q, ans in (r.get("answers") or {}).items():
            note = notes.get(q, {}).get("notes") if isinstance(notes.get(q), dict) else None
            plan_log(sid, f"Controller answered ({one(headers.get(q) or q)}): {one(ans)}" + (f"; note: {one(note)}" if note else ""))
    except Exception:
        hook_error()


# --------------------------------------------------------------------------- launching the pilot

def known_projects():
    """The directories Claude Code has a record of, from the projects list in its .claude.json (only the
    path, whether its workspace trust was accepted, and when a session last started there), newest first."""
    cfg = PROFILE / ".claude.json" if os.environ.get("CLAUDE_CONFIG_DIR") else Path.home() / ".claude.json"
    projects = (read_json(cfg, {}) or {}).get("projects") or {}
    out = [{"path": p.replace("\\", "/"), "trusted": bool(v.get("hasTrustDialogAccepted")), "lastStart": v.get("lastStartTime")}
           for p, v in projects.items() if isinstance(v, dict) and Path(p).is_dir()]
    return sorted(out, key=lambda x: x["lastStart"] or 0, reverse=True)


def project_dir(path):
    """<profile>/projects/<path with every non-alphanumeric character as ->: where its transcripts are."""
    return PROFILE / "projects" / re.sub(r"[^A-Za-z0-9]", "-", path)


TITLE_RE = re.compile(rb'"(customTitle|agentName|aiTitle)":"((?:[^"\\]|\\.)*)"')


def project_sessions(path, limit=40):
    """The sessions run in a directory, newest first, each with the title Claude Code gave it (a custom
    title, then an agent name, then its own AI title, then the first prompt) and whether it is running."""
    files = sorted(project_dir(path).glob("*.jsonl"), key=lambda f: f.stat().st_mtime, reverse=True)[:limit]
    live = {r.get("sessionId") for r in agents() if r.get("pid")}
    out = []
    for f in files:
        size = f.stat().st_size
        with open(f, "rb") as fh:
            head = fh.read(64 * 1024)
            fh.seek(max(0, size - 128 * 1024))
            tail = fh.read()
        titles = {}
        for kind, val in TITLE_RE.findall(head + tail):
            titles[kind.decode()] = val
        title = next((titles[k] for k in ("customTitle", "agentName", "aiTitle") if k in titles), None)
        title = json.loads(b'"' + title + b'"') if title else None
        if not title:
            for line in head.splitlines():
                if b'"type":"user"' in line:
                    try:
                        c = json.loads(line).get("message", {}).get("content")
                    except ValueError:
                        continue
                    if isinstance(c, str) and c.strip():
                        title = c.strip().splitlines()[0][:100]
                        break
        out.append({"sessionId": f.stem, "title": title or "(untitled)", "updated": f.stat().st_mtime, "size": size,
                    "live": f.stem in live})
    return out


def launch(path, resume=None, where="terminal", message="", as_pilot=True):
    """Starts a pilot session in a directory Claude Code knows, new or resuming one of its sessions: in a new
    Windows Terminal tab (interactive, yours to type into), or in the background (reached by attach or
    Remote Control). The pilot plugin is loaded either way."""
    known = {p["path"]: p for p in known_projects()}
    path = (path or "").replace("\\", "/")
    if path not in known:
        raise ValueError("that directory is not one Claude Code has a record of")
    if resume and (not re.fullmatch(r"[0-9a-f-]{36}", resume) or not (project_dir(path) / f"{resume}.jsonl").exists()):
        raise ValueError("no such session in that directory")
    text = ((f"/pilot:pilot {message}" if message.strip() else "/pilot:pilot") if as_pilot else message).strip()
    args = ["--plugin-dir", str(HERE / "plugins" / "pilot")] + (["--resume", resume] if resume else [])
    if where == "background":
        if not known[path]["trusted"]:
            raise ValueError("a background session needs the directory's workspace trust accepted: open claude there once")
        job = backgrounded(claude("--bg", "-n", f"pilot-{Path(path).name}"[:40], *args, stdin=text or None, cwd=path, timeout=120))
        return {"where": "background", "id": job}
    # Windows Terminal splits its own command line on ';', so the first message travels in a file instead.
    first = ""
    if text:
        f = STATE / "launch" / f"{int(time.time() * 1000)}.txt"
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(text, encoding="utf-8")
        first = f" (Get-Content -Raw -LiteralPath '{f}')"
    quoted = " ".join("'" + a.replace("'", "''") + "'" for a in args)
    wt = shutil.which("wt.exe") or shutil.which("wt")
    if not wt:
        raise ValueError("Windows Terminal (wt.exe) is not on PATH")
    command = f"& claude {quoted}{first}".replace(";", "\\;")      # wt reads a bare ';' as its own separator
    subprocess.Popen([wt, "-w", "0", "new-tab", "--title", "pilot", "-d", path, "pwsh", "-NoExit", "-Command", command],
                     env=claude_env(), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return {"where": "terminal"}


# --------------------------------------------------------------------------- dashboard

class Hub:
    """The one watcher behind every open dashboard. It stats the watched files every second, rebuilds the
    state when they move (asking claude only when a session file moved, or every 30 s, which is what
    catches a session that died without writing), raises notices, and wakes every waiting stream."""

    def __init__(self):
        self.cond = threading.Condition()
        self.version, self.body, self.dirty = 0, "{}", True
        self.notices = Notices()
        self.running = set()             # summaries in flight, by key

    def poke(self):
        self.dirty = True

    def run(self):
        last_fp, asked, sids = (None, None), 0.0, []
        while True:
            try:
                fp = fingerprint(sids)
                if fp != last_fp or self.dirty or time.time() - asked > 30:
                    if fp[0] != last_fp[0] or time.time() - asked > 30:
                        _agents_cache["at"], asked = 0, time.time()
                    else:
                        _agents_cache["at"] = time.time()   # no session file moved: reuse claude's answer
                    self.dirty = False
                    snap = snapshot()
                    self.notices.update(snap)
                    snap["notifications"] = self.notices.view()
                    snap["summaries"] = read_json(SUMMARIES, {})
                    snap["running"] = sorted(self.running)
                    sids = [s["sessionId"] for s in snap["workers"] + snap["pilots"] if s.get("sessionId")]
                    last_fp = fingerprint(sids)
                    body = json.dumps(snap)
                    if body != self.body:
                        with self.cond:
                            self.body, self.version = body, self.version + 1
                            self.cond.notify_all()
            except Exception:
                with open(STATE / "dashboard-errors.log", "a", encoding="utf-8") as f:
                    f.write(f"--- {time.strftime('%Y-%m-%dT%H:%M:%S')}\n{traceback.format_exc()}")
            time.sleep(1)

    def wait(self, seen, timeout=15):
        with self.cond:
            self.cond.wait_for(lambda: self.version != seen, timeout)
            return self.version, self.body


HUB = Hub()
STATIC = {"/": ("index.html", "text/html; charset=utf-8"), "/app.js": ("app.js", "text/javascript; charset=utf-8"),
          "/app.css": ("app.css", "text/css; charset=utf-8")}


class Handler(http.server.BaseHTTPRequestHandler):
    preview = False
    def log_message(self, fmt, *a):
        if os.environ.get("PILOT_DEBUG"):
            with open(STATE / "requests.log", "a", encoding="utf-8") as f:
                f.write(f"{time.strftime('%H:%M:%S')} {threading.current_thread().name} {fmt % a}\n")

    def send(self, code, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(data)

    def local(self):
        """Only a page served from this machine's loopback name, so a DNS-rebinding page cannot read
        transcripts or act on workers."""
        port = self.server.server_address[1]
        return self.headers.get("Host") in (f"127.0.0.1:{port}", f"localhost:{port}")

    def sse(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()

    def emit(self, event, data):
        self.wfile.write(f"event: {event}\ndata: {json.dumps(data) if not isinstance(data, str) else data}\n\n".encode())
        self.wfile.flush()

    def do_GET(self):
        if not self.local():
            return self.send(403, {"error": "forbidden"})
        path = self.path.split("?")[0]
        try:
            if path in STATIC:
                name, ctype = STATIC[path]
                return self.send(200, (WEB / name).read_bytes(), ctype)
            if path == "/api/state":
                return self.send(200, HUB.body.encode())
            if path == "/api/stream":
                return self.state_stream()
            m = re.match(r"^/api/sessions/([0-9a-f-]{36})(?:/agents/([0-9a-f]{6,40}))?/stream$", path)
            if m:
                return self.session_stream(m.group(1), m.group(2))
            m = re.match(r"^/api/sessions/([0-9a-f-]{36})/agents$", path)
            if m:
                return self.send(200, subagents(m.group(1)))
            if path == "/api/projects":
                return self.send(200, known_projects())
            if path == "/api/projects/sessions":
                q = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
                p = (q.get("path") or [""])[0].replace("\\", "/")
                if p not in {x["path"] for x in known_projects()}:
                    return self.send(404, {"error": "not a project Claude Code knows"})
                return self.send(200, project_sessions(p))
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            return
        return self.send(404, {"error": "not found"})

    def state_stream(self):
        """Server-sent events: the whole state, sent again only when it changed."""
        self.sse()
        seen = -1
        while True:
            version, body = HUB.wait(seen)
            if version != seen:
                self.emit("state", body)
                seen = version
            else:
                self.wfile.write(b": ping\n\n")
                self.wfile.flush()

    def session_stream(self, sid, agent_id):
        """Server-sent events: one transcript's last 400 events, then each new batch as it is written."""
        main = transcript(sid)
        path = main.parent / sid / "subagents" / f"agent-{agent_id}.jsonl" if main and agent_id else main
        if not path or not path.exists():
            return self.send(404, {"error": "no such transcript"})
        self.sse()
        start = max(0, path.stat().st_size - 8 * 1024 * 1024)    # a long session opens on its last 8 MB
        if start:
            with open(path, "rb") as f:
                f.seek(start)
                start += len(f.readline())
        events, offset = read_events(path, start)
        self.emit("backlog", {"events": events[-400:], "skipped": max(0, len(events) - 400), "truncated": start > 0})
        if self.preview:                      # one look, then close: an open stream never lets headless Edge settle
            self.wfile.write(b"retry: 600000\n\n")
            self.wfile.flush()
            return
        pinged = time.time()
        while True:
            time.sleep(1)
            if path.stat().st_size > offset:
                events, offset = read_events(path, offset)
                if events:
                    self.emit("events", events)
                    pinged = time.time()
            if time.time() - pinged > 15:
                self.wfile.write(b": ping\n\n")
                self.wfile.flush()
                pinged = time.time()

    def do_POST(self):
        # Writes only from this page: same host, and a header a cross-site form cannot set.
        host = self.headers.get("Host", "")
        if not self.local() or self.headers.get("X-Pilot") != "1" or self.headers.get("Origin", f"http://{host}") != f"http://{host}":
            return self.send(403, {"error": "forbidden"})
        size = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(size) or b"{}") if size else {}
        m = re.match(r"^/api/workers/([a-z0-9-]+)/(stop|say|budget)$", self.path)
        if m:
            w = load(m.group(1))
            if not w:
                return self.send(404, {"error": "no such worker"})
            if m.group(2) == "stop":
                r = stop(w["name"])
                HUB.poke()
                return self.send(200 if r.returncode == 0 else 502, {"ok": r.returncode == 0, "output": (r.stdout + r.stderr).strip()})
            if m.group(2) == "budget":
                tokens = body.get("tokens")
                if not isinstance(tokens, int) or tokens < 0:
                    return self.send(400, {"error": "tokens must be a non-negative whole number"})
                set_budget(w["name"], tokens)
                HUB.poke()
                return self.send(200, {"ok": True, "budget": tokens})
            try:
                w = say(w["name"], str(body.get("text") or ""))
            except (ValueError, SystemExit) as e:
                return self.send(409, {"error": str(e)})
            HUB.poke()
            return self.send(200, {"ok": True, "id": w["id"], "sessionId": w["sessionId"]})
        m = re.match(r"^/api/summary/([a-z0-9-]+|\*)$", self.path)
        if m:
            key = m.group(1)
            if key in HUB.running:
                return self.send(409, {"error": "a summary of that is already running"})
            HUB.running.add(key)
            HUB.poke()
            try:
                return self.send(200, summarize(None if key == "*" else key))
            except (RuntimeError, SystemExit, subprocess.TimeoutExpired) as e:
                return self.send(502, {"error": str(e)})
            finally:
                HUB.running.discard(key)
                HUB.poke()
        if self.path == "/api/intercom":
            names = body.get("to") if isinstance(body.get("to"), list) else []
            known = {p["name"] for p in pilots()} | {agent_name(w) for w in registered()}
            if not names or any(n not in known for n in names):
                return self.send(400, {"error": f"send only to live pilots and workers: {', '.join(sorted(known)) or 'none'}"})
            try:
                out = intercom(names, str(body.get("text") or ""))
            except (ValueError, subprocess.TimeoutExpired) as e:
                return self.send(502, {"error": str(e)})
            HUB.poke()
            return self.send(200, {"ok": True, "result": out})
        if self.path == "/api/launch":
            try:
                out = launch(str(body.get("path") or ""), body.get("resume") or None, "background" if body.get("where") == "background" else "terminal",
                             str(body.get("message") or ""), bool(body.get("asPilot", True)))
            except (ValueError, SystemExit) as e:
                return self.send(409, {"error": str(e)})
            HUB.poke()
            return self.send(200, out)
        if self.path == "/api/notifications/read":
            HUB.notices.read(body.get("ids"))
            HUB.poke()
            return self.send(200, {"ok": True})
        return self.send(404, {"error": "not found"})


def cmd_dashboard(a):
    """The dashboard. --preview is a second one for checking a change to pilot itself beside the live one: on
    5182 unless --port says otherwise, it raises no notices and writes none, and a session page's stream
    sends its backlog and closes, so headless Edge settles and can take a screenshot (README)."""
    STATE.mkdir(parents=True, exist_ok=True)
    port = a.port or (5182 if a.preview else 5181)
    if a.preview:
        HUB.notices.update = lambda snap: []
        HUB.notices.save = lambda: None
        Handler.preview = True
    threading.Thread(target=HUB.run, daemon=True).start()
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    srv.daemon_threads = True
    print(f"pilot dashboard{' (preview)' if a.preview else ''}: http://127.0.0.1:{port}/  (Ctrl-C stops it)")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


def main():
    ap = argparse.ArgumentParser(prog="pilot.py", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("start")
    s.add_argument("name")
    s.add_argument("--brief", required=True, help="a file, or - for stdin")
    s.add_argument("--repo")
    s.add_argument("--model")
    s.add_argument("--effort", choices=["low", "medium", "high", "xhigh", "max"])
    s.add_argument("--budget", type=int, help="weighted tokens; default from ~/.pilot/pilot.json")
    s.add_argument("--pilot", help="the pilot session's name, so the worker knows whose messages to follow")
    s.add_argument("--after", action="append", help="a worker this one waits for (shown in the graph)")
    s.set_defaults(fn=cmd_start)
    s = sub.add_parser("list")
    s.add_argument("--json", action="store_true")
    s.set_defaults(fn=cmd_list)
    for name, fn in (("result", cmd_result), ("stop", cmd_stop), ("rm", cmd_rm)):
        s = sub.add_parser(name)
        s.add_argument("name")
        s.set_defaults(fn=fn)
    s = sub.add_parser("say")
    s.add_argument("name")
    s.add_argument("text", nargs="+", help="the message, or - for stdin")
    s.set_defaults(fn=cmd_say)
    s = sub.add_parser("tell", help="your words to live sessions through the intercom, mid-turn included")
    s.add_argument("names", nargs="+", help="session names, as `claude agents` shows them: `DPS1 scaffold` (flight and leg) for a worker")
    s.add_argument("--message", "-m", required=True, help="the message, or - for stdin")
    s.set_defaults(fn=cmd_tell)
    s = sub.add_parser("budget")
    s.add_argument("name")
    s.add_argument("tokens", type=int)
    s.set_defaults(fn=cmd_budget)
    s = sub.add_parser("summary")
    s.add_argument("name", nargs="?")
    s.set_defaults(fn=cmd_summary)
    s = sub.add_parser("plan")
    s.add_argument("--title", help="the plan's title, used when it is created")
    s.add_argument("--session", help="a pilot session id; default: the session this runs in")
    s.set_defaults(fn=cmd_plan)
    s = sub.add_parser("dashboard")
    s.add_argument("--port", type=int, help="default 5181, or 5182 with --preview")
    s.add_argument("--preview", action="store_true", help="a second dashboard for checking a change: no notices, session streams close")
    s.set_defaults(fn=cmd_dashboard)
    sub.add_parser("hook-budget").set_defaults(fn=cmd_hook_budget)
    sub.add_parser("hook-ask").set_defaults(fn=cmd_hook_ask)
    a = ap.parse_args()
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a.fn(a)


if __name__ == "__main__":
    main()
