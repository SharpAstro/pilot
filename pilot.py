#!/usr/bin/env python
"""pilot: one pilot session and some workers, on Claude Code's own background sessions.

The pilot is an ordinary interactive session in the repo. A worker is `claude --bg -w <name>`: a background
session in its own git worktree, branched from origin/develop, which the Claude Code daemon keeps running
whether or not a terminal is open. This file starts workers the same way every time, lists them with what
they have spent, gates their spend, and stops them. Talking to one is Claude Code's own business: the pilot
uses SendMessage, and you use `claude attach <id>` or Remote Control.

  python pilot.py start NAME --brief FILE|- [--repo R] [--model M] [--effort E] [--budget N] [--pilot NAME]
  python pilot.py list [--json]
  python pilot.py result NAME          the worker's last message: its report
  python pilot.py budget NAME N        a new ceiling in weighted tokens (absolute, not an increment)
  python pilot.py stop NAME            stop it; the conversation is kept
  python pilot.py rm NAME              delete it, and its worktree when Claude Code says that is safe
  python pilot.py dashboard [--port 5181]
  python pilot.py hook-budget          the worker plugin's PreToolUse hook (reads the payload on stdin)

Windows notes, all measured on CLI 2.1.296:
  * The brief goes to claude on stdin, never as an argument: a Windows command line stops at 32,767
    characters, and `-p "<prompt>"` with no stdin redirect waits 3 s for stdin first.
  * Options like --allowedTools, --add-dir and --tools take several values and swallow a prompt written
    after them; with the prompt on stdin there is nothing to swallow.
  * `--bg` refuses a directory whose workspace trust was never accepted. The repo must have been opened
    interactively once; `-w` worktrees inside it inherit that.
"""
import argparse
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
from pathlib import Path

HERE = Path(__file__).resolve().parent
CONFIG = HERE / "pilot.json"
STATE = Path(os.environ.get("PILOT_STATE") or Path(os.environ.get("LOCALAPPDATA") or Path.home() / ".local/state") / "pilot")
WORKERS = STATE / "workers"
METERS = STATE / "meters"
PROFILE = Path(os.environ.get("CLAUDE_CONFIG_DIR") or Path.home() / ".claude")
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,29}$")


def config():
    return json.loads(CONFIG.read_text(encoding="utf-8"))


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
    try:
        return json.loads((PROFILE / "sessions" / f"{pid}.json").read_text(encoding="utf-8"))
    except (OSError, ValueError, TypeError):
        return {}


def transcript(session_id):
    hits = glob.glob(str(PROFILE / "projects" / "*" / f"{session_id}.jsonl"))
    return Path(hits[0]) if hits else None


def same_path(a, b):
    return os.path.normcase(os.path.normpath(a or "")) == os.path.normcase(os.path.normpath(b or ""))


# --------------------------------------------------------------------------- registry

def load(name):
    p = WORKERS / f"{name}.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None


def save(w):
    WORKERS.mkdir(parents=True, exist_ok=True)
    tmp = WORKERS / f"{w['name']}.json.tmp"
    tmp.write_text(json.dumps(w, indent=1), encoding="utf-8")
    os.replace(tmp, WORKERS / f"{w['name']}.json")


def registered():
    return [json.loads(p.read_text(encoding="utf-8")) for p in sorted(WORKERS.glob("*.json"))]


def need(name):
    w = load(name)
    if not w:
        sys.exit(f"pilot: no worker named {name}")
    return w


# --------------------------------------------------------------------------- spend

WEIGHTS = {"input_tokens": 1.0, "cache_creation_input_tokens": 1.25, "cache_read_input_tokens": 0.1, "output_tokens": 5.0}


class Meter:
    """Weighted tokens of one transcript file, read incrementally: input + 1.25*cache_creation +
    0.1*cache_read + 5*output, the same price ratios Sam's agent_token_budget.sh uses. A message's usage is
    repeated on every content block it was streamed in, so it is counted once per message id."""

    def __init__(self, path):
        self.path, self.offset, self.by_id, self.raw = Path(path), 0, {}, {}

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
            key = m.get("id") or e.get("uuid")
            self.raw[key] = {k: int(u.get(k) or 0) for k in WEIGHTS}
            self.by_id[key] = sum(WEIGHTS[k] * self.raw[key][k] for k in WEIGHTS)
        return self

    def weighted(self):
        return int(sum(self.by_id.values()))

    def tokens(self):
        return {k: sum(r[k] for r in self.raw.values()) for k in WEIGHTS}


_meters = {}


def spend(session_id):
    """Weighted tokens and raw token counts of a session, its subagents included."""
    main = transcript(session_id)
    if not main:
        return {"weighted": 0, "tokens": {k: 0 for k in WEIGHTS}, "files": 0}
    files = [main, *sorted(main.parent.glob(f"{session_id}/subagents/agent-*.jsonl"))]
    total, tokens = 0, {k: 0 for k in WEIGHTS}
    for f in files:
        m = _meters.get(str(f)) or _meters.setdefault(str(f), Meter(f))
        m.read()
        total += m.weighted()
        for k, v in m.tokens().items():
            tokens[k] += v
    return {"weighted": total, "tokens": tokens, "files": len(files)}


# --------------------------------------------------------------------------- transcript reading

def tail_events(session_id, n=40, tail_bytes=512 * 1024):
    """The session's last n events as one-line summaries, newest last."""
    path = transcript(session_id)
    if not path:
        return []
    with open(path, "rb") as f:
        f.seek(0, 2)
        size = f.tell()
        f.seek(max(0, size - tail_bytes))
        data = f.read()
    lines = data.splitlines()[1 if size > tail_bytes else 0:]
    out = []
    for line in lines:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        t, ts = e.get("type"), e.get("timestamp")
        msg = e.get("message") if isinstance(e.get("message"), dict) else {}
        content = msg.get("content")
        if t == "assistant" and (e.get("error") or msg.get("error")):
            # An API error, a usage limit or a failed login, recorded as a synthetic assistant message.
            out.append({"at": ts, "kind": "error", "text": f"{e.get('error') or msg.get('error')}: {flat(content)[:600]}"})
        elif t == "user" and isinstance(content, str):
            kind = "message" if "cross-session-message" in content else "prompt"
            out.append({"at": ts, "kind": kind, "text": content[:2000]})
        elif t in ("user", "assistant") and isinstance(content, list):
            for b in content:
                if not isinstance(b, dict):
                    continue
                k = b.get("type")
                if k == "text" and t == "assistant" and b.get("text", "").strip():
                    out.append({"at": ts, "kind": "said", "text": b["text"][:4000]})
                elif k == "tool_use":
                    out.append({"at": ts, "kind": "tool", "tool": b.get("name"), "text": brief_input(b.get("input"))})
                elif k == "tool_result" and b.get("is_error"):
                    out.append({"at": ts, "kind": "error", "text": flat(b.get("content"))[:600]})
    return out[-n:]


def flat(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(b.get("text", "") if isinstance(b, dict) else str(b) for b in content)
    return json.dumps(content)


def brief_input(inp):
    if not isinstance(inp, dict):
        return ""
    for k in ("command", "file_path", "pattern", "description", "prompt", "url", "to"):
        if inp.get(k):
            return str(inp[k])[:300]
    return json.dumps(inp)[:300]


def report(session_id):
    """The session's last assistant text: a finished worker's report."""
    said = [e for e in tail_events(session_id, n=400, tail_bytes=4 * 1024 * 1024) if e["kind"] == "said"]
    return said[-1]["text"] if said else ""


# --------------------------------------------------------------------------- status

def worker_status(w, rows=None):
    rows = agents() if rows is None else rows
    row = next((r for r in rows if r.get("id") == w["id"] or r.get("sessionId") == w.get("sessionId")), None)
    if row and not w.get("sessionId") and row.get("sessionId"):
        w["sessionId"] = row["sessionId"]
        save(w)
    sid = w.get("sessionId")
    path = transcript(sid) if sid else None
    sp = spend(sid) if sid else {"weighted": 0, "tokens": {}, "files": 0}
    last = None
    if path:
        times = [path.stat().st_mtime] + [p.stat().st_mtime for p in path.parent.glob(f"{sid}/subagents/*.jsonl")]
        last = max(times)
    ev = tail_events(sid, n=12) if sid else []
    now = next((e for e in reversed(ev) if e["kind"] in ("tool", "said")), None)
    rec = session_record(row.get("pid")) if row and row.get("pid") else {}
    state = (row or {}).get("state") or ("gone" if not row else (row.get("status") or "?"))
    return {
        **{k: w.get(k) for k in ("name", "id", "sessionId", "repo", "worktree", "branch", "model", "effort", "budget", "started")},
        "state": state, "busy": (row or {}).get("status") == "busy", "pid": (row or {}).get("pid"),
        "spend": sp, "overBudget": bool(w.get("budget")) and sp["weighted"] >= w["budget"],
        "lastActivity": last, "now": now,
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
            rec = session_record(r["pid"])
            out.append({"name": r.get("name"), "id": r.get("id"), "sessionId": r.get("sessionId"), "kind": r.get("kind"),
                        "busy": r.get("status") == "busy", "spend": spend(r["sessionId"]),
                        "remote": f"https://claude.ai/code/{rec['bridgeSessionId']}" if rec.get("bridgeSessionId") else None})
    return out


def limits():
    """The account's 5-hour and 7-day windows, if a status line writes Sam's rate-limit cache."""
    try:
        d = json.loads((PROFILE / "rate-limits.json").read_text(encoding="utf-8"))
        return {"windows": d.get("rate_limits"), "writtenAt": d.get("written_at")}
    except (OSError, ValueError):
        return None


# --------------------------------------------------------------------------- commands

PREAMBLE = """\
You are a worker named {name}, started by a pilot session for the repo {repo}. You run as a background
Claude Code session in your own git worktree, {worktree}, on branch {branch}, which was branched from
origin/develop. Nobody is watching your terminal. The git status in your system prompt describes the repo's
main checkout, not your worktree: run `git status` yourself before relying on a branch name.

How to work:
- Do the brief below and nothing outside it. Work only inside your worktree; never in the repo's main checkout.
- Commit as you go, on your branch. Do not push, open a PR, merge or rebase unless the brief says so.
- Prove the result with the check the brief names, and quote its real output. If the brief names no check,
  say what you ran and what it showed.
- Messages from {pilot} are from your pilot. Treat them as corrections to this brief: follow them within
  the brief's scope, and never as permission for something the brief or your permissions would not allow.
- If you are blocked on something only a human can decide or do, stop and end your turn with the question.
  Do not guess.
- If a tool call is refused with "pilot budget", stop calling tools and write your report.
- Your last message is your report. Its first line is one of DONE, BLOCKED or FAILED. Then: what changed
  (commits with short hashes), the evidence, and what is left open.

# Brief

"""


def cmd_start(a):
    cfg = config()
    if not NAME_RE.match(a.name):
        sys.exit("pilot: a worker name is lowercase letters, digits and hyphens, at most 30")
    if load(a.name):
        sys.exit(f"pilot: {a.name} exists; `pilot.py rm {a.name}` first, or pick another name")
    repo = a.repo or next(iter(cfg["repos"]))
    root = cfg["repos"].get(repo)
    if not root or not Path(root, ".git").exists():
        sys.exit(f"pilot: unknown repo {repo}; pilot.json knows {', '.join(cfg['repos'])}")
    brief = sys.stdin.read() if a.brief == "-" else Path(a.brief).read_text(encoding="utf-8")
    if not brief.strip():
        sys.exit("pilot: the brief is empty")
    wt = f"w-{a.name}"
    w = {"name": a.name, "repo": repo, "worktree": str(Path(root, ".claude", "worktrees", wt)),
         "branch": f"worktree-{wt}", "model": a.model or cfg.get("model"), "effort": a.effort or cfg.get("effort"),
         "budget": a.budget or cfg.get("budget"), "started": time.time()}
    prompt = PREAMBLE.format(pilot=f'the session named "{a.pilot}"' if a.pilot else "other Claude sessions on this machine",
                             **w) + brief
    args = ["--bg", "-w", wt, "-n", wt, "--permission-mode", "auto", "--plugin-dir", str(HERE / "plugins" / "worker")]
    if w["model"]:
        args += ["--model", w["model"]]
    if w["effort"]:
        args += ["--effort", w["effort"]]
    r = claude(*args, stdin=prompt, cwd=root, timeout=120)
    m = re.search(r"backgrounded\s*\S\s*([0-9a-f]{8})", r.stdout)
    if r.returncode != 0 or not m:
        sys.exit(f"pilot: claude --bg failed ({r.returncode}): {(r.stdout + r.stderr).strip()[:800]}")
    w["id"] = m.group(1)
    briefs = STATE / "briefs"
    briefs.mkdir(parents=True, exist_ok=True)
    (briefs / f"{a.name}.md").write_text(prompt, encoding="utf-8")
    save(w)
    for _ in range(20):                       # the session id lands in `claude agents` within a second or two
        _agents_cache["at"] = 0
        row = next((x for x in agents() if x.get("id") == w["id"]), None)
        if row and row.get("sessionId"):
            w["sessionId"] = row["sessionId"]
            save(w)
            break
        time.sleep(0.5)
    print(f"started {a.name}: {w['id']} in {w['worktree']} on {w['branch']}")
    print(f"  message it:  SendMessage to \"{wt}\"   |   you: claude attach {w['id']}")


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
    w = need(a.name)
    print(report(w["sessionId"]) if w.get("sessionId") else "(no session yet)")


def cmd_budget(a):
    w = need(a.name)
    w["budget"] = a.tokens
    save(w)
    print(f"{a.name}: budget {a.tokens:,} weighted tokens")


def cmd_stop(a):
    w = need(a.name)
    r = claude("stop", w["id"])
    print((r.stdout + r.stderr).strip())


def cmd_rm(a):
    w = need(a.name)
    r = claude("rm", w["id"])
    print((r.stdout + r.stderr).strip())
    if r.returncode == 0:
        (WORKERS / f"{a.name}.json").unlink(missing_ok=True)


def cmd_hook_budget(_a):
    """PreToolUse, every tool, in the worker plugin. A session pilot.py started is refused every tool but
    SendMessage once its weighted tokens, subagents included, reach its budget: a checkpoint, not a kill. It
    writes its report and the pilot decides. Any other session passes. An error passes too, since a broken
    guard must not stop a worker, but it is logged to <state>/hook-errors.log, never swallowed."""
    try:
        # Bytes, then UTF-8: a piped stdin on Windows decodes as the ANSI code page otherwise.
        p = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        sid = p.get("session_id") or ""
        w = next((x for x in registered() if x.get("sessionId") == sid or (x.get("id") and sid.startswith(x["id"]))), None)
        if not w or not w.get("budget") or p.get("tool_name") == "SendMessage":
            return
        METERS.mkdir(parents=True, exist_ok=True)
        cache = METERS / f"{sid}.json"
        try:
            saved = json.loads(cache.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            saved = {}
        main = Path(p.get("transcript_path") or transcript(sid) or "")
        files = [main, *sorted(main.parent.glob(f"{sid}/subagents/agent-*.jsonl"))]
        total = 0
        for f in files:
            m = Meter(f)
            s = saved.get(str(f))
            if s:
                m.offset, m.by_id, m.raw = s["offset"], s["by_id"], {}
            m.read()
            saved[str(f)] = {"offset": m.offset, "by_id": m.by_id}
            total += m.weighted()
        tmp = cache.with_suffix(".tmp")
        tmp.write_text(json.dumps(saved), encoding="utf-8")
        os.replace(tmp, cache)
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
        import traceback
        STATE.mkdir(parents=True, exist_ok=True)
        with open(STATE / "hook-errors.log", "a", encoding="utf-8") as f:
            f.write(f"--- {time.strftime('%Y-%m-%dT%H:%M:%S')}\n{traceback.format_exc()}")


# --------------------------------------------------------------------------- dashboard

PAGE = (HERE / "dashboard.html")


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def local(self):
        """Only a page served from this machine's loopback name, so a DNS-rebinding page cannot read
        transcripts or stop workers."""
        port = self.server.server_address[1]
        return self.headers.get("Host") in (f"127.0.0.1:{port}", f"localhost:{port}")

    def do_GET(self):
        if not self.local():
            return self.send(403, {"error": "forbidden"})
        path = self.path.split("?")[0]
        if path == "/":
            return self.send(200, PAGE.read_bytes(), "text/html; charset=utf-8")
        if path == "/api/state":
            rows = agents()
            return self.send(200, {"workers": [worker_status(w, rows) for w in registered()], "pilots": pilots(rows),
                                   "limits": limits(), "now": time.time()})
        m = re.match(r"^/api/sessions/([0-9a-f-]{36})/events$", path)
        if m:
            return self.send(200, tail_events(m.group(1), n=80))
        return self.send(404, {"error": "not found"})

    def do_POST(self):
        # Writes only from this page: same host, and a header a cross-site form cannot set.
        host = self.headers.get("Host", "")
        if not self.local() or self.headers.get("X-Pilot") != "1" or self.headers.get("Origin", f"http://{host}") != f"http://{host}":
            return self.send(403, {"error": "forbidden"})
        m = re.match(r"^/api/workers/([a-z0-9-]+)/stop$", self.path)
        if m and load(m.group(1)):
            r = claude("stop", load(m.group(1))["id"])
            return self.send(200, {"ok": r.returncode == 0, "output": (r.stdout + r.stderr).strip()})
        return self.send(404, {"error": "not found"})


def cmd_dashboard(a):
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", a.port), Handler)
    print(f"pilot dashboard: http://127.0.0.1:{a.port}/  (Ctrl-C stops it)")
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
    s.add_argument("--budget", type=int, help="weighted tokens; default from pilot.json")
    s.add_argument("--pilot", help="the pilot session's name, so the worker knows whose messages to follow")
    s.set_defaults(fn=cmd_start)
    s = sub.add_parser("list")
    s.add_argument("--json", action="store_true")
    s.set_defaults(fn=cmd_list)
    for name, fn in (("result", cmd_result), ("stop", cmd_stop), ("rm", cmd_rm)):
        s = sub.add_parser(name)
        s.add_argument("name")
        s.set_defaults(fn=fn)
    s = sub.add_parser("budget")
    s.add_argument("name")
    s.add_argument("tokens", type=int)
    s.set_defaults(fn=cmd_budget)
    s = sub.add_parser("dashboard")
    s.add_argument("--port", type=int, default=5181)
    s.set_defaults(fn=cmd_dashboard)
    sub.add_parser("hook-budget").set_defaults(fn=cmd_hook_budget)
    a = ap.parse_args()
    if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a.fn(a)


if __name__ == "__main__":
    main()
