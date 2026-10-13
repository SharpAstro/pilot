"""The offline suite: pilot.py's own logic against fixture plans, worker records, transcripts and usage figures,
with no Claude Code, no daemon and no account. Run it from the repo root:

    python -m unittest discover tests

Every test gets an empty state folder and an empty profile, so nothing here reads or writes the machine's
real ones: PILOT_STATE and CLAUDE_CONFIG_DIR point into a temporary folder before pilot.py is imported, and
`claude agents` is replaced by a fixed list of rows.
"""
import contextlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import types
import unittest
import unittest.mock
from pathlib import Path

ROOT = Path(tempfile.mkdtemp(prefix="pilot-tests-"))
os.environ["PILOT_STATE"] = str(ROOT / "state")
os.environ["CLAUDE_CONFIG_DIR"] = str(ROOT / "profile")
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import pilot  # noqa: E402  (the environment above has to be set first)

pilot.CONFIG = ROOT / "pilot.json"


def tearDownModule():
    shutil.rmtree(ROOT, ignore_errors=True)


class Case(unittest.TestCase):
    """A fresh state folder, profile and config for each test, and no live sessions."""

    def setUp(self):
        for d in (pilot.STATE, pilot.PROFILE):
            shutil.rmtree(d, ignore_errors=True)
            d.mkdir(parents=True)
        pilot.CONFIG.unlink(missing_ok=True)
        pilot._meters.clear()
        pilot._replayed.clear()
        pilot._git.clear()
        pilot._stamps.clear()
        pilot._schedules.clear()
        self.rows = []
        self._agents, pilot.agents = pilot.agents, lambda: self.rows

    def tearDown(self):
        pilot.agents = self._agents

    def config(self, **cfg):
        pilot.CONFIG.write_text(json.dumps(cfg), encoding="utf-8")

    def plan(self, sid, text):
        pilot.PLANS.mkdir(parents=True, exist_ok=True)
        p = pilot.plan_path(sid)
        p.write_text(text, encoding="utf-8")
        return p

    def worker(self, name, **kw):
        w = {"name": name, "id": f"job-{name}", "sessionId": f"sess-{name}", "repo": "web-app", "branch": None,
             "budget": 10_000_000, "budget0": 10_000_000, **kw}
        pilot.save(w)
        return w

    def transcript(self, sid, records, project="C--repo"):
        d = pilot.PROFILE / "projects" / project
        d.mkdir(parents=True, exist_ok=True)
        p = d / f"{sid}.jsonl"
        p.write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")
        return p

    def hook(self, fn, payload):
        """Runs a hook command with its payload on stdin, as Claude Code would, and returns what it printed."""
        out, old = io.StringIO(), sys.stdin
        sys.stdin = types.SimpleNamespace(buffer=io.BytesIO(json.dumps(payload).encode("utf-8")))
        try:
            with contextlib.redirect_stdout(out):
                fn(None)
        finally:
            sys.stdin = old
        self.assertFalse((pilot.STATE / "hook-errors.log").exists(), "the hook raised and logged an error")
        return out.getvalue()

    def log_of(self, sid):
        return pilot.parse_plan(pilot.plan_path(sid).read_text(encoding="utf-8"))["log"]


def usage(mid, inp=0, cw=0, cr=0, out=0, ts="2026-10-13T01:00:00Z"):
    return {"type": "assistant", "timestamp": ts, "message": {"id": mid, "usage": {
        "input_tokens": inp, "cache_creation_input_tokens": cw, "cache_read_input_tokens": cr, "output_tokens": out}}}


def tool_use(tid, name, inp):
    return {"type": "assistant", "message": {"content": [{"type": "tool_use", "id": tid, "name": name, "input": inp}]}}


def tool_result(tid, text="ok", error=False, **extra):
    return {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": tid, "content": text,
                                                      "is_error": error}]}, **extra}


PLAN = """# Flight plan: Export fixes

Flight: WA1

Objective: Fix the export of rotated pages,
and prove it with a golden test.

Pilot session: s1

## Waiting on the controller

- **Push `feat/export`?** Only CI can prove it. Options: Yes | No. Recommend: Yes.
- **Raise fix's budget?** It is at 9.8M. Options: 16M | No. Recommend: 16M.

The exporter rounds rotation twice; that is the bug.

## Route

| Leg | Goal | After | Done when |
|---|---|---|---|
| `design` | Write the design note | - | `DESIGN.md` reviewed |
| fix | Fix `a \\| b` rounding | design, scaffold | `dotnet test` passes |

## Log

- 2026-10-13 09:00 Plan opened.
- 2026-10-13 09:05 Decided: one leg per concern.
  Co-pilot agreed.
"""


class PlanTests(Case):
    def test_parses_title_flight_objective_and_route(self):
        p = pilot.parse_plan(PLAN)
        self.assertEqual(p["title"], "Export fixes")
        self.assertEqual(p["flight"], "WA1")
        self.assertEqual(p["objective"], "Fix the export of rotated pages, and prove it with a golden test.")
        self.assertEqual([l["leg"] for l in p["legs"]], ["design", "fix"])
        self.assertEqual(p["legs"][0]["after"], [])
        self.assertEqual(p["legs"][1]["after"], ["design", "scaffold"])
        self.assertEqual(p["legs"][1]["goal"], "Fix `a | b` rounding", "an escaped pipe stays in its cell")
        self.assertEqual(p["legs"][1]["doneWhen"], "`dotnet test` passes")

    def test_a_wrapped_log_entry_is_one_entry(self):
        self.assertEqual(pilot.parse_plan(PLAN)["log"][-1], "2026-10-13 09:05 Decided: one leg per concern. Co-pilot agreed.")

    def test_waiting_section_ends_at_the_paragraph_after_its_list(self):
        p = pilot.parse_plan(PLAN)
        self.assertEqual(p["waiting"].count("\n- "), 1)
        self.assertTrue(p["waiting"].startswith("- **Push"))
        self.assertNotIn("rounds rotation", p["waiting"])
        self.assertEqual(p["notes"], "The exporter rounds rotation twice; that is the bug.")

    def test_header_lines_are_not_notes(self):
        p = pilot.parse_plan(PLAN.replace("Flight: WA1", "Flight: WA1\n\nTracking: https://claude.ai/code/artifact/x"))
        self.assertEqual(p["tracking"], "https://claude.ai/code/artifact/x")
        self.assertEqual(p["notes"], "The exporter rounds rotation twice; that is the bug.")

    def test_no_waiting_section(self):
        text = PLAN.split("## Waiting")[0] + "## Route" + PLAN.split("## Route")[1]
        self.assertEqual(pilot.parse_plan(text)["waiting"], "")

    def test_flight_numbers_count_per_repo(self):
        self.assertEqual(pilot.flight_code("web-app"), "WA1")
        self.assertEqual(pilot.flight_code("web-app"), "WA2")
        self.assertEqual(pilot.flight_code("pilot"), "PIL1")
        self.assertEqual(pilot.flight_code("drawboard_pdf.sdk"), "DPS1")

    def test_an_old_plan_gets_a_flight_number_once(self):
        self.plan("old", "# Flight plan: Old\n\nObjective: x\n\n## Log\n")
        self.assertEqual(pilot.plan_flight("old", "web-app"), "WA1")
        self.assertEqual(pilot.plan_flight("old", "web-app"), "WA1")
        self.assertEqual(pilot.plan_path("old").read_text(encoding="utf-8").count("Flight:"), 1)

    def test_plan_command_makes_the_template_once(self):
        repo = ROOT / "web-app"
        repo.mkdir(exist_ok=True)
        subprocess.run(["git", "init", "-q", str(repo)], check=True)
        self.config(repos={"web-app": str(repo)})
        cwd = os.getcwd()
        os.chdir(repo)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                pilot.cmd_plan(types.SimpleNamespace(session="s2", title="Export fixes"))
                pilot.cmd_plan(types.SimpleNamespace(session="s2", title="ignored the second time"))
        finally:
            os.chdir(cwd)
        p = pilot.parse_plan(pilot.plan_path("s2").read_text(encoding="utf-8"))
        self.assertEqual((p["title"], p["flight"]), ("Export fixes", "WA1"))
        self.assertEqual(len(p["log"]), 1)

    def test_plan_log_appends_and_records_in_the_black_box(self):
        self.plan("s1", PLAN)
        pilot.plan_log("s1", "Leg fix took off")
        self.assertTrue(self.log_of("s1")[-1].endswith("Leg fix took off"))
        e = [json.loads(l) for l in pilot.BLACKBOX.read_text(encoding="utf-8").splitlines()]
        self.assertEqual([(x["actor"], x["flight"], x["plan"]) for x in e], [("pilot.py", "WA1", "s1")])

    def test_black_box_merges_what_pilots_wrote_themselves_once(self):
        self.plan("s1", PLAN)
        pilot.plan_log("s1", "Controller answered (Push): Yes")
        view = pilot.blackbox_view()
        texts = [e["text"] for e in view]
        self.assertEqual(texts.count("Controller answered (Push): Yes"), 1)
        self.assertIn("Decided: one leg per concern. Co-pilot agreed.", texts)
        actors = {e["text"]: e["actor"] for e in view}
        self.assertEqual(actors["Controller answered (Push): Yes"], "controller")
        self.assertEqual(actors["Decided: one leg per concern. Co-pilot agreed."], "pilot")


CONNECTED = """# Flight plan: Theme tokens

Flight: TIA2

Objective: Ship the tokens.

Pilot session: s2

## Route

| Leg | Goal | After | Done when |
|---|---|---|---|
| build | Build the tokens | - | tests pass |

## Log

- 2026-10-13 09:00 Plan opened.
"""


class ConnectionTests(Case):
    def setUp(self):
        super().setUp()
        # Each leg's git, as collateral_now would read it: read, no commits past its base, no PR.
        self.git = {"ahead": 0, "prs": [], "remotes": []}
        self._git_fns = pilot.collateral_now, pilot.collateral
        pilot.collateral_now, pilot.collateral = (lambda w: self.git), (lambda w, ttl=20: self.git)

    def tearDown(self):
        pilot.collateral_now, pilot.collateral = self._git_fns
        super().tearDown()

    def connect(self, *flights, remove=False, session="s1"):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            pilot.cmd_connect(types.SimpleNamespace(session=session, flights=list(flights), remove=remove))
        return out.getvalue()

    def start(self, sid="s1"):
        with unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": sid}):
            with self.assertRaises(SystemExit) as e:
                pilot.cmd_start(types.SimpleNamespace(name="leg", after=None))
        return str(e.exception)

    def landed(self, leg="build", plan="s2", **final):
        pilot.write_json(pilot.ARCHIVE / f"{leg}-1.json", {"name": leg, "plan": plan, "archivedAt": 1, "repo": "web-app",
                         "branch": f"worktree-w-{leg}", **final,
                         "final": {"reportHead": "DONE", "checklist": [{"status": "completed", "subject": "x"}]}})

    def test_the_connects_line_parses_and_is_neither_notes_nor_waiting(self):
        text = PLAN.replace("Recommend: 16M.\n", "Recommend: 16M.\nConnects: TIA2, DPS1\n")
        p = pilot.parse_plan(text)
        self.assertEqual(p["connects"], ["TIA2", "DPS1"])
        self.assertNotIn("Connects", p["waiting"])
        self.assertEqual(p["waiting"].count("\n- "), 1, "the waiting list ends at the header line")
        self.assertEqual(p["notes"], "The exporter rounds rotation twice; that is the bug.")
        self.assertEqual(pilot.parse_plan(PLAN)["connects"], [])

    def test_connect_files_the_line_once_under_the_flight_and_logs_it(self):
        self.plan("s1", PLAN)
        self.plan("s2", CONNECTED)
        self.plan("s3", CONNECTED.replace("TIA2", "DPS1"))
        self.assertIn("recorded: Connects: TIA2", self.connect("TIA2"))
        self.connect("TIA2,DPS1")
        text = pilot.plan_path("s1").read_text(encoding="utf-8")
        self.assertEqual(text.count("Connects:"), 1)
        self.assertIn("Flight: WA1\n\nConnects: TIA2, DPS1\n", text)
        self.assertEqual(pilot.parse_plan(text)["connects"], ["TIA2", "DPS1"])
        self.assertTrue(self.log_of("s1")[-1].endswith("Connects from TIA2, DPS1: its legs take off once TIA2, DPS1 have landed"))

    def test_connect_refuses_an_unknown_flight_and_itself(self):
        self.plan("s1", PLAN)
        self.plan("s2", CONNECTED)
        with self.assertRaises(SystemExit) as e:
            self.connect("TIA9")
        self.assertIn("no flight plan has the number TIA9", str(e.exception))
        self.assertIn("TIA2", str(e.exception), "it lists the flights there are")
        with self.assertRaises(SystemExit) as e:
            self.connect("WA1")
        self.assertIn("cannot connect from itself", str(e.exception))
        self.assertEqual(pilot.plan_path("s1").read_text(encoding="utf-8"), PLAN)

    def test_connect_remove_takes_a_connection_off(self):
        self.plan("s1", PLAN)
        self.plan("s2", CONNECTED)
        self.plan("s3", CONNECTED.replace("TIA2", "DPS1"))
        self.connect("TIA2", "DPS1")
        self.connect("TIA2", remove=True)
        self.assertEqual(pilot.parse_plan(pilot.plan_path("s1").read_text(encoding="utf-8"))["connects"], ["DPS1"])
        self.assertIn("recorded: no connections", self.connect("DPS1", remove=True))
        text = pilot.plan_path("s1").read_text(encoding="utf-8")
        self.assertNotIn("Connects:", text)
        self.assertIn("Flight: WA1\n\nObjective:", text)
        self.assertIn("No longer connects from DPS1", self.log_of("s1")[-1])
        with self.assertRaises(SystemExit) as e:
            self.connect("DPS1", remove=True)
        self.assertIn("does not connect from DPS1", str(e.exception))

    def test_start_refuses_while_the_connection_has_not_landed(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2"))
        self.plan("s2", CONNECTED)
        msg = self.start()
        self.assertIn("WA1 connects from TIA2", msg)
        self.assertIn("TIA2 (Theme tokens): 0 of 1 legs landed; still open: build: planned", msg)
        self.assertIn("put it to the controller in `## Waiting on the controller`", msg)
        self.assertFalse(pilot.WORKERS.exists() and list(pilot.WORKERS.glob("*.json")), "nothing took off")

    def test_start_refuses_a_connection_no_flight_has(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA9"))
        self.plan("s2", CONNECTED)
        msg = self.start()
        self.assertIn("TIA9: no flight plan has that number", msg)

    def test_start_goes_on_once_the_connection_has_landed(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2"))
        self.plan("s2", CONNECTED)
        self.landed()
        self.assertIsNone(pilot.connection_hold("s1"))
        pilot.write_json(pilot.DAEMON_LOCK, {"pid": os.getpid(), "procStartFt": "1"})
        self.assertIn("daemon lock", self.start(), "past the connection, to the next check")

    def test_landed_waits_for_the_pull_requests(self):
        st = [{"leg": "build", "w": {"approach": {"stage": "cleared", "label": "cleared to land", "pr": 7}}, "cls": "good", "label": "cleared to land",
               "git": {"ahead": 0, "prs": []}}]
        self.assertEqual(pilot.flight_landed(st), (False, ["build: #7 cleared to land"]))
        st[0]["w"]["approach"]["stage"] = "gate"
        self.assertEqual(pilot.flight_landed(st), (True, []))
        self.assertEqual(pilot.flight_landed([]), (False, ["no legs on its route yet"]))

    def test_a_done_leg_whose_pull_requests_are_not_read_has_not_landed(self):
        leg = lambda git: [{"leg": "build", "w": {"reportHead": "DONE"}, "cls": "good", "label": "landed", "git": git}]
        for git in (None, {"pending": True}, {"error": "boom"}, {"ahead": 0, "remotes": ["origin/feat/x"], "prs": None}):
            self.assertEqual(pilot.flight_landed(leg(git)), (False, ["build: its pull requests not read yet"]), git)

    def test_start_refuses_while_the_connections_git_cannot_be_read(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2"))
        self.plan("s2", CONNECTED)
        self.landed()
        self.git = None                 # collateral_now found nothing, or raised
        self.assertIn("build: its pull requests not read yet", self.start())
        pilot.collateral_now = lambda w: 1 / 0
        self.assertIn("build: its pull requests not read yet", self.start())

    def test_a_failed_or_missing_gh_leaves_the_pull_requests_unknown(self):
        with unittest.mock.patch.object(pilot, "gh_json", lambda *a: None):
            self.assertIsNone(pilot.leg_prs(ROOT, ["origin/feat/x"]))
        with unittest.mock.patch.object(pilot.shutil, "which", lambda name: None):
            self.assertIsNone(pilot.leg_prs(ROOT, ["origin/feat/x"]))
        self.assertEqual(pilot.leg_prs(ROOT, []), [], "no remote branch holds it: no PR, known")

    def test_a_legs_commits_land_only_on_a_merged_pull_request(self):
        leg = lambda git, w=None: [{"leg": "build", "w": w or {"reportHead": "DONE"}, "cls": "good", "label": "landed", "git": git}]
        self.assertEqual(pilot.flight_landed(leg({"ahead": 2, "prs": []})), (False, ["build: 2 commits on no merged pull request"]))
        open_pr = {"number": 7, "state": "OPEN"}
        self.assertFalse(pilot.flight_landed(leg({"ahead": 1, "prs": [open_pr]}))[0])
        self.assertEqual(pilot.flight_landed(leg({"ahead": 2, "prs": [{"number": 7, "state": "MERGED"}]})), (True, []))
        self.assertEqual(pilot.flight_landed(leg({"ahead": 0, "prs": []})), (True, []), "no commits past its base, or all on it: landed")

    def test_a_removed_legs_deleted_branch_lands_only_with_no_commits(self):
        leg = lambda commits: [{"leg": "build", "w": {"final": {"reportHead": "DONE", "commits": commits}}, "cls": "good", "label": "landed",
                                "git": {"gone": True, "repo": "web-app", "branch": "worktree-w-build"}}]
        self.assertEqual(pilot.flight_landed(leg("")), (True, []))
        self.assertEqual(pilot.flight_landed(leg("abc1234 the fix")), (False, ["build: its branch is deleted, so its commits cannot be read"]))

    def test_start_refuses_while_the_connections_commits_are_not_merged(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2"))
        self.plan("s2", CONNECTED)
        self.landed()
        self.git = {"ahead": 3, "prs": [], "remotes": []}      # DONE, unpushed
        self.assertIn("build: 3 commits on no merged pull request", self.start())
        self.git = {"ahead": 3, "prs": [{"number": 7, "state": "MERGED"}], "remotes": ["origin/feat/x"]}
        self.assertIsNone(pilot.connection_hold("s1"))

    def test_the_dashboard_gets_each_connection_and_whether_it_landed(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2, TIA9"))
        self.plan("s2", CONNECTED)
        p = next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s1")
        self.assertEqual([(c["flight"], c["known"], c["landed"]) for c in p["connections"]], [("TIA2", True, False), ("TIA9", False, False)])
        self.landed()
        p = next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s1")
        self.assertEqual((p["connections"][0]["landed"], p["connections"][0]["state"]), (True, "landed"))


class PriorityTests(Case):
    """The controller's order of flights, in priority.json: it orders the dashboard's flights, and only
    set_priority (the dashboard's POST /api/priority) writes it."""

    def flights(self, *sids):
        """Plans for `sids`, the first the most recently touched, so with no order they list in that order."""
        now = time.time()
        for i, sid in enumerate(sids):
            os.utime(self.plan(sid, PLAN.replace("Export fixes", sid)), (now - i * 60, now - i * 60))

    def order(self):
        return [p["sessionId"] for p in pilot.plans_view([], [])]

    def test_no_file_lists_by_last_touched(self):
        self.flights("a", "b", "c")
        self.assertEqual(pilot.priority(), [])
        self.assertEqual(self.order(), ["a", "b", "c"])

    def test_a_corrupt_file_is_no_order(self):
        self.flights("a", "b", "c")
        for junk in ("{not json", '{"a": 1}', '["c", 7, null]'):
            pilot.PRIORITY.write_text(junk, encoding="utf-8")
            want = ["c", "a", "b"] if junk.startswith("[") else ["a", "b", "c"]
            self.assertEqual(self.order(), want, junk)

    def test_the_order_lists_first_and_new_flights_follow_in_todays_order(self):
        self.flights("new1", "a", "new2", "b", "c")
        pilot.write_json(pilot.PRIORITY, ["c", "gone", "a"])
        self.assertEqual(self.order(), ["c", "a", "new1", "new2", "b"], "a stale id is passed over")

    def test_a_write_drops_stale_ids_and_keeps_the_rest_of_the_old_order(self):
        self.flights("a", "b", "c", "d")
        pilot.write_json(pilot.PRIORITY, ["gone", "d", "c"])
        self.assertEqual(pilot.set_priority(["b", "a", "nope"]), ["b", "a", "d", "c"])
        self.assertEqual(pilot.read_json(pilot.PRIORITY), ["b", "a", "d", "c"])
        self.assertEqual(self.order(), ["b", "a", "d", "c"])

    def test_a_write_takes_only_a_list_of_ids(self):
        for bad in (None, "a", ["a", 1], ["../x"]):
            with self.assertRaises(ValueError):
                pilot.set_priority(bad)
        self.assertFalse(pilot.PRIORITY.exists())

    def test_a_woken_pilots_flight_keeps_its_place(self):
        self.flights("a", "old", "b")
        pilot.write_json(pilot.PRIORITY, ["b", "old", "a"])
        pilot.move_plan("old", "new")
        self.assertEqual(self.order(), ["b", "new", "a"])
        self.assertEqual(pilot.read_json(pilot.PRIORITY), ["b", "new", "a"])

    def test_a_woken_pilot_the_order_never_placed_writes_no_order(self):
        self.flights("a", "old")
        pilot.move_plan("old", "new")
        self.assertFalse(pilot.PRIORITY.exists())

    def test_the_endpoint_applies_a_new_order(self):
        import threading
        import urllib.error
        import urllib.request
        self.flights("a", "b", "c")
        srv = pilot.http.server.ThreadingHTTPServer(("127.0.0.1", 0), pilot.Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            def post(body, headers={"X-Pilot": "1"}):
                req = urllib.request.Request(f"http://127.0.0.1:{srv.server_address[1]}/api/priority", json.dumps(body).encode(),
                                             {"Content-Type": "application/json", **headers}, method="POST")
                try:
                    with urllib.request.urlopen(req) as r:
                        return r.status, json.loads(r.read())
                except urllib.error.HTTPError as e:
                    with e:
                        return e.code, json.loads(e.read())
            self.assertEqual(post({"order": ["c", "a", "b"]}), (200, {"ok": True, "order": ["c", "a", "b"]}))
            self.assertEqual(self.order(), ["c", "a", "b"])
            self.assertEqual(post({"order": "c"})[0], 400)
            self.assertEqual(post({"order": ["a", "b", "c"]}, headers={})[0], 403, "only the dashboard's page writes it")
            self.assertEqual(self.order(), ["c", "a", "b"])
        finally:
            srv.shutdown()
            srv.server_close()


class WakeTests(Case):
    def test_a_sleeping_pilot_wakes_as_a_copy_and_its_plan_and_legs_move_with_it(self):
        self.plan("old-sid", "# Flight plan: x\n\nPilot session: old-sid\n\n## Log\n\n- 2026-10-14 08:00 Plan opened.\n")
        self.worker("leg-a", plan="old-sid")
        self.rows = [{"name": "pilot-x", "sessionId": "old-sid", "kind": "background", "state": "done", "pid": None, "cwd": str(ROOT)}]
        calls = []
        saved = pilot.claude, pilot.backgrounded, pilot.session_of
        pilot.claude = lambda *a, **kw: calls.append((a, kw.get("stdin"))) or None
        pilot.backgrounded, pilot.session_of = (lambda r: "job12345"), (lambda job: "new-sid")
        try:
            self.assertEqual(pilot.wake_pilot("old-sid", "Leg leg-a has stopped (DONE)."), "new-sid")
        finally:
            pilot.claude, pilot.backgrounded, pilot.session_of = saved
        self.assertEqual(calls[0][0][:3], ("--bg", "--resume", "old-sid"))
        self.assertIn("Leg leg-a has stopped (DONE).", calls[0][1])
        self.assertFalse(pilot.plan_path("old-sid").exists())
        self.assertIn("Pilot session: new-sid", pilot.plan_path("new-sid").read_text(encoding="utf-8"))
        self.assertEqual(pilot.load("leg-a")["plan"], "new-sid")
        self.assertIn("Pilot woken by pilot.py as new-sid", self.log_of("new-sid")[-1])

    def test_a_live_or_interactive_pilot_is_not_woken(self):
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n")
        for row in ({"sessionId": "p1", "kind": "background", "pid": 42}, {"sessionId": "p1", "kind": "interactive", "pid": None}):
            self.rows = [row]
            self.assertIsNone(pilot.wake_pilot("p1", "why"))


class LogTests(Case):
    def test_log_stamps_the_time_now(self):
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n- 2026-10-13 20:52 Plan opened.\n")
        with contextlib.redirect_stdout(io.StringIO()):
            pilot.cmd_log(types.SimpleNamespace(text=["Decided:", "three", "legs."], session="p1"))
        self.assertEqual(self.log_of("p1")[-1], f"{time.strftime('%Y-%m-%d %H:%M')} Decided: three legs.")
        with self.assertRaises(SystemExit):
            pilot.cmd_log(types.SimpleNamespace(text=["x"], session="no-such-plan"))


class UnreadTests(Case):
    def test_a_leg_that_stopped_after_its_pilot_last_logged_it_is_unread(self):
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n- 2026-10-13 20:52 Asked leg-a for a docs commit.\n"
                        "- 2026-10-13 21:30 Other news.\n")
        w = self.worker("leg-a", plan="p1")
        at = time.mktime(time.strptime("2026-10-13 20:52", "%Y-%m-%d %H:%M"))
        self.assertTrue(pilot.unread(w, False, at + 600))     # finished ten minutes after; later lines name others
        self.assertFalse(pilot.unread(w, False, at + 60))     # within the log's minute, and its slack
        self.assertFalse(pilot.unread(w, True, at + 600))     # still busy
        pilot.plan_log("p1", "leg-a LANDED: checked and pushed.")
        self.assertFalse(pilot.unread(w, False, time.time() - 5))

    def test_untimed_records_after_its_turn_do_not_make_a_leg_unread(self):
        """Claude Code appends last-prompt and cost-state records to a transcript after the turn ends, which moves
        the file's time past the pilot's log line. When the leg stopped is its last timestamped event."""
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n- 2026-10-13 21:35 leg-a LANDED: checked.\n")
        w = self.worker("leg-a", plan="p1")
        stopped = time.mktime(time.strptime("2026-10-13 21:31", "%Y-%m-%d %H:%M"))
        ts = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(stopped))
        p = self.transcript("sess-leg-a", [
            {"type": "assistant", "timestamp": ts, "message": {"id": "m1", "content": [{"type": "text", "text": "DONE: all of it"}]}},
            {"type": "last-prompt"}, {"type": "cost-state"}])
        os.utime(p, (stopped + 1500, stopped + 1500))         # the file touched 25 minutes after its turn
        row = {"id": w["id"], "sessionId": w["sessionId"], "state": "done", "status": "idle"}
        self.assertFalse(pilot.worker_status(w, [row])["unread"])
        # A message after the pilot's 21:35 line is something it has not seen.
        p.write_text(p.read_text(encoding="utf-8") + json.dumps({"type": "assistant", "timestamp": time.strftime(
            "%Y-%m-%dT%H:%M:%SZ", time.gmtime(stopped + 1800)), "message": {"id": "m2", "content": [
            {"type": "text", "text": "DONE: and one more thing"}]}}) + "\n", encoding="utf-8")
        pilot._meters.clear()
        self.assertTrue(pilot.worker_status(w, [row])["unread"])

    def test_a_leg_its_plan_never_names_or_without_a_plan_is_not_unread(self):
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n- 2026-10-13 20:52 Plan opened.\n")
        self.assertFalse(pilot.unread(self.worker("leg-a", plan="p1"), False, time.time()))
        self.assertFalse(pilot.unread(self.worker("leg-b"), False, time.time()))


ROUND_2_BUGS = """BUGS: 2

PR #7 at abcdef12, round 1. Two defects.

## Checklist
- [x] build green: CI
- [ ] no hand sites left: one remains (B2)

## Bugs
### B1 Saving drops the last row
src/a.cs:10. Trace.
### B2 One hand site left
src/b.cs:4.

## Questions
- does the cache survive a reload?

## Read
src/a.cs, src/b.cs
"""


class ReviewTests(Case):
    def pr(self, head="abcdef1234", state="OPEN", ci="passing", number=7, **kw):
        return {"number": number, "state": state, "head": head, "ci": ci, "url": f"https://x/pull/{number}", **kw}

    def test_a_round_file_states_its_bugs_and_lists_them(self):
        r = pilot.parse_round(ROUND_2_BUGS)
        self.assertEqual((r["bugs"], r["questions"]), (2, 1))
        self.assertEqual(r["titles"], ["Saving drops the last row", "One hand site left"])
        self.assertEqual(pilot.parse_round("BUGS: 0\n\n## Checklist\n- [x] all\n")["bugs"], 0)
        with self.assertRaises(ValueError):
            pilot.parse_round(ROUND_2_BUGS.replace("BUGS: 2", "BUGS: 0"))      # says none over a list of two
        with self.assertRaises(ValueError):
            pilot.parse_round("Looks good to me.\n")

    def test_where_a_pr_stands(self):
        stage = lambda reviews, pr: (pilot.approach(reviews, [pr]) or {}).get("stage")
        r1 = {"round": 1, "pr": 7, "head": "abcdef1234", "bugs": 2}
        ok = {"round": 2, "pr": 7, "head": "1111111111", "bugs": 0}
        self.assertIsNone(pilot.approach([], []))
        self.assertEqual(stage([], self.pr()), "approach")
        self.assertEqual(stage([r1], self.pr()), "goaround")
        self.assertEqual(stage([r1], self.pr(head="1111111111")), "approach")      # fixes pushed: round 2 due
        self.assertEqual(stage([r1, ok], self.pr(head="1111111111")), "cleared")
        self.assertEqual(stage([r1, ok], self.pr(head="1111111111", ci="running")), "holding")
        self.assertEqual(stage([r1, ok], self.pr(head="1111111111", ci="failing")), "goaround")
        self.assertEqual(stage([r1, ok], self.pr(head="2222222222")), "approach")  # pushed after acceptance
        self.assertEqual(stage([r1, {**ok, "bugs": 1}], self.pr(head="1111111111")), "tower")
        self.assertEqual(stage([r1], self.pr(state="MERGED")), "gate")
        self.assertEqual(stage([r1], self.pr(number=8)), "approach")              # a round of another PR

    def test_a_pr_its_team_reviews_waits_for_their_approval(self):
        stage = lambda pr: (pilot.approach([{"round": 1, "pr": 7, "head": "1111111111", "bugs": 0}], [pr]) or {}).get("stage")
        team = dict(head="1111111111", reviewer="team")
        self.assertEqual(stage(self.pr(**team, review="REVIEW_REQUIRED")), "clearance")   # accepted and green: theirs next
        self.assertEqual(stage(self.pr(**team, review="CHANGES_REQUESTED")), "clearance")
        self.assertEqual(stage(self.pr(**team, review="APPROVED")), "cleared")
        self.assertEqual(stage(self.pr(head="1111111111", reviewer="self")), "cleared")   # the flight is its review

    def test_review_records_the_round_and_logs_it(self):
        self.config(repos={"web-app": str(ROOT)})
        self.plan("p1", "# Flight plan: x\n\n## Log\n\n- 2026-10-13 20:52 Plan opened.\n")
        self.worker("leg-a", plan="p1", branch="b")
        f = ROOT / "round.md"
        f.write_text(ROUND_2_BUGS, encoding="utf-8")
        old = pilot.collateral_now
        pilot.collateral_now = lambda w: {"head": "abcdef12", "prs": [self.pr()]}
        try:
            run = lambda **kw: pilot.cmd_review(types.SimpleNamespace(name="leg-a", file=str(f), round=None, pr=None, post=False, **kw))
            with contextlib.redirect_stdout(io.StringIO()) as out:
                run()
            self.assertIn("go-around", out.getvalue())
            w = pilot.load("leg-a")
            self.assertEqual([(r["round"], r["bugs"], r["head"]) for r in w["reviews"]], [(1, 2, "abcdef1234")])
            self.assertTrue(Path(w["reviews"][0]["file"]).exists())
            self.assertIn("Review round 1 of leg-a (PR #7 at abcdef12): 2 bug(s): go-around", self.log_of("p1")[-1])
            pilot.collateral_now = lambda w: {"head": "99999999", "prs": [self.pr()]}
            with self.assertRaises(SystemExit):     # the branch has commits the PR does not
                run()
            pilot.collateral_now = lambda w: {"head": "11111111", "prs": [self.pr(head="1111111111")]}
            f.write_text(ROUND_2_BUGS.replace("BUGS: 2", "BUGS: 1").replace("### B2 One hand site left\n", ""), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as ex:
                run()                               # round 2 still finds one: the rounds are spent
            self.assertEqual(ex.exception.code, 2)
            self.assertEqual(len(pilot.load("leg-a")["reviews"]), 2)
            self.assertIn("to the tower", self.log_of("p1")[-1])
        finally:
            pilot.collateral_now = old


class FlightTimeTests(Case):
    """Departure, ETA, schedule and delay: active time with long gaps capped, the projection, the schedule's file."""

    def at(self, sid, minutes, start="2026-10-13T09:00:00"):
        """A transcript with one record at each of these minutes past 09:00 UTC; returns 09:00's timestamp."""
        t0 = pilot.datetime.datetime.fromisoformat(start + "+00:00")
        self.transcript(sid, [{"type": "assistant", "timestamp": (t0 + pilot.datetime.timedelta(minutes=m)).isoformat().replace("+00:00", "Z")}
                              for m in minutes])
        return t0.timestamp()

    def leg(self, state, active=0, done=0, total=0, started=1000.0, last=None, name="x"):
        return {"state": state, "active": active, "done": done, "total": total, "started": started, "last": last, "name": name}

    def test_a_long_gap_counts_at_most_the_cap(self):
        self.assertEqual(pilot.GAP_CAP, 600)
        self.assertEqual(pilot.active_seconds([0, 60, 120]), 120)
        self.assertEqual(pilot.active_seconds([0, 60, 60 + 8 * 3600, 60 + 8 * 3600 + 30]), 60 + 600 + 30)   # asleep overnight
        self.assertEqual(pilot.active_seconds([300, 0, 60]), 300)        # in any order
        self.assertEqual(pilot.active_seconds([5]), 0)
        self.assertEqual(pilot.active_seconds([0, 3600], cap=60), 60)

    def test_a_legs_active_time_is_read_from_all_its_sessions(self):
        self.at("s-a", [0, 2, 4])
        self.at("s-a2", [4, 300, 301])                  # its `say` copy repeats minute 4, then picks up hours later
        w = {"name": "a", "sessions": [{"sessionId": "s-a"}, {"sessionId": "s-a2"}], "started": 1.0,
             "reportHead": "DONE", "checklist": [{"status": "completed"}]}
        t = pilot.leg_timing(w)
        self.assertEqual(t["state"], "landed")
        self.assertEqual(t["active"], 4 * 60 + 600 + 60)
        self.assertEqual((t["done"], t["total"]), (1, 1))
        self.assertEqual(pilot.leg_timing(None), {"state": "unflown"})
        self.worker("a", sessions=[{"sessionId": "s-a"}, {"sessionId": "s-a2"}])   # a live status counts its sessions
        self.assertEqual(pilot.leg_timing({**w, "sessions": 2})["active"], 4 * 60 + 600 + 60)

    def test_eta_of_a_leg_in_the_air_at_its_own_rate(self):
        t = pilot.project_eta([self.leg("flying", active=1200, done=2, total=6)], now=10_000)
        self.assertEqual(t["departed"], 1000.0)
        self.assertEqual(t["eta"], 10_000 + 4 * 600)       # 4 items left, 10 minutes each
        self.assertIn("4 of 6 items left", t["basis"])

    def test_legs_not_flown_take_the_landed_legs_mean(self):
        legs = [self.leg("landed", active=3600, done=6, total=6, started=500.0),
                self.leg("landed", active=1800, done=3, total=3),
                self.leg("flying", active=600, done=0, total=5),    # none done: the landed legs' rate, 10m an item
                self.leg("unflown"), self.leg("unflown")]
        t = pilot.project_eta(legs, now=10_000)
        self.assertEqual(t["departed"], 500.0)
        self.assertEqual(t["eta"], 10_000 + 5 * 600 + 2 * 2700)
        self.assertIn("2 legs not flown at 45m each", t["basis"])

    def test_no_eta_without_anything_to_time_by(self):
        t = pilot.project_eta([self.leg("flying", active=600, done=0, total=4), self.leg("unflown")], now=10_000)
        self.assertIsNone(t["eta"])
        self.assertIn("no ETA yet", t["noEta"])
        t = pilot.project_eta([self.leg("flying", active=600, done=1, total=4), self.leg("unflown")], now=10_000)
        self.assertIsNone(t["eta"])                         # the leg in the air can be timed, the one not flown cannot
        self.assertIn("1 not flown", t["noEta"])
        t = pilot.project_eta([self.leg("unflown")], now=10_000)
        self.assertEqual((t["departed"], t["eta"], t["noEta"]), (None, None, "not departed"))

    def test_a_flight_whose_legs_all_landed_shows_its_arrival(self):
        t = pilot.project_eta([self.leg("landed", active=60, last=5000.0), self.leg("landed", active=60, last=7000.0)], now=10_000)
        self.assertEqual((t["arrived"], t["eta"]), (7000.0, None))

    def test_the_schedule_is_set_once_survives_a_reload_and_is_never_rewritten(self):
        self.plan("p1", PLAN)
        self.assertIsNone(pilot.schedule("p1", None))                     # nothing to schedule by yet
        self.assertFalse(pilot.SCHEDULES.exists())
        self.assertEqual(pilot.schedule("p1", 50_000.0), 50_000.0)
        self.assertEqual(pilot.read_json(pilot.SCHEDULES)["p1"]["eta"], 50_000.0)   # on disk: a restart reads it back
        self.assertEqual(pilot.schedule("p1", 90_000.0), 50_000.0)         # a later ETA never moves it
        logged = [l for l in self.log_of("p1") if "Schedule set" in l]
        self.assertEqual(len(logged), 1)
        self.assertIn(time.strftime("%H:%M", time.localtime(50_000.0)), logged[0])

    def test_a_missing_or_corrupt_schedule_file_is_no_schedules(self):
        pilot.SCHEDULES.write_text("{not json", encoding="utf-8")
        self.assertEqual(pilot.schedule("p2", 70_000.0), 70_000.0)
        self.assertEqual(pilot.read_json(pilot.SCHEDULES)["p2"]["eta"], 70_000.0)
        pilot.SCHEDULES.write_text("[1, 2]", encoding="utf-8")
        self.assertEqual(pilot.schedule("p2", 80_000.0), 80_000.0)

    def test_a_woken_pilots_flight_keeps_its_schedule(self):
        self.plan("old", PLAN)
        self.assertEqual(pilot.schedule("old", 50_000.0), 50_000.0)
        pilot.move_plan("old", "new")
        self.assertEqual(pilot.schedule("new", 90_000.0), 50_000.0, "its delay still counts from its first ETA")
        self.assertNotIn("old", pilot.read_json(pilot.SCHEDULES))

    def test_a_preview_keeps_its_schedules_in_memory(self):
        self.assertEqual(pilot.schedule("p3", 60_000.0, persist=False), 60_000.0)
        self.assertEqual(pilot.schedule("p3", 61_000.0, persist=False), 60_000.0)
        self.assertFalse(pilot.SCHEDULES.exists())

    def test_delay_is_the_eta_now_against_the_schedule(self):
        self.plan("p1", PLAN)
        t0 = self.at("s-a", [0, 10, 20])                     # 20 active minutes, 2 of 4 items: 20 more to go
        w = {"name": "fix", "plan": "p1", "sessionId": "s-a", "state": "working", "started": t0,
             "checklist": [{"status": "completed"}, {"status": "completed"}, {"status": "pending"}, {"status": "pending"}]}
        legs = [{"leg": "fix"}]
        first = pilot.flight_times("p1", legs, [w])
        self.assertEqual(first["departed"], t0)
        self.assertEqual(first["scheduled"], first["eta"])
        self.assertAlmostEqual(first["delay"], 0, delta=1)
        self.at("s-a", [0, 10, 20, 30])                      # 10 more active minutes, no item done: it slips
        late = pilot.flight_times("p1", legs, [w])
        self.assertEqual(late["scheduled"], first["scheduled"])
        self.assertGreater(late["delay"], 9 * 60)
        done = {**w, "checklist": [{"status": "completed"}] * 4, "reportHead": "DONE", "state": "done"}
        early = pilot.flight_times("p1", legs, [done])
        self.assertIsNone(early["eta"])
        self.assertEqual(early["arrived"], t0 + 30 * 60)
        self.assertLess(early["delay"], 0)                   # landed before its schedule: early is negative


class SpendTests(Case):
    def test_weighted_tokens_count_each_message_once_across_files_and_subagents(self):
        main = self.transcript("w1", [usage("m1", inp=100, out=10), usage("m1", inp=100, out=10),
                                      usage("m2", cw=1000, cr=10000)])
        sub = main.parent / "w1" / "subagents"
        sub.mkdir(parents=True)
        (sub / "agent-a1.jsonl").write_text(json.dumps(usage("m3", out=200)) + "\n", encoding="utf-8")
        self.transcript("w1-copy", [usage("m1", inp=100, out=10), usage("m4", inp=7)])   # a `say` copy repeats m1
        s = pilot.spend(["w1", "w1-copy"])
        # m1: 100 + 5*10 = 150; m2: 1.25*1000 + 0.1*10000 = 2250; m3: 5*200 = 1000; m4: 7
        self.assertEqual(s["weighted"], 150 + 2250 + 1000 + 7)
        self.assertEqual(s["tokens"]["output_tokens"], 210)

    def test_a_partial_last_line_is_read_again_once_complete(self):
        p = self.transcript("w2", [usage("m1", inp=10)])
        line = json.dumps(usage("m2", inp=5))
        with open(p, "a", encoding="utf-8") as f:
            f.write(line[:20])
        m = pilot.Meter(p).read()
        self.assertEqual(len(m.entries), 1)
        with open(p, "a", encoding="utf-8") as f:
            f.write(line[20:] + "\n")
        self.assertEqual(sum(e[0] for e in m.read().entries.values()), 15)


class TranscriptTests(Case):
    def test_checklist_from_the_task_files(self):
        d = pilot.PROFILE / "tasks" / "w1"
        d.mkdir(parents=True)
        for i, st in ((1, "completed"), (2, "in_progress"), (3, "deleted"), (10, "pending")):
            pilot.write_json(d / f"{i}.json", {"id": str(i), "subject": f"item {i}", "status": st})
        items = pilot.checklist(["w1"])
        self.assertEqual([i["id"] for i in items], ["1", "2", "10"])
        self.assertEqual(len(pilot.open_items(items)), 2)

    def test_a_finished_checklist_is_rebuilt_from_the_transcript(self):
        self.transcript("w1", [
            tool_use("t1", "TaskCreate", {"subject": "write test", "activeForm": "Writing test"}),
            tool_result("t1", "Task #1 created", toolUseResult={"task": {"id": "1"}}),
            tool_use("t2", "TaskCreate", {"subject": "fix bug"}),
            tool_result("t2", "Task #2 created"),
            tool_use("t3", "TaskCreate", {"subject": "never made"}),
            tool_result("t3", "<tool_use_error>could not create the task</tool_use_error>", error=True),
            tool_use("t4", "TaskUpdate", {"taskId": "1", "status": "completed"}),
            tool_result("t4", "Updated task #1 status"),
            tool_use("t5", "TaskUpdate", {"taskId": "2", "status": "deleted"}),
            tool_result("t5", "Updated task #2 deleted"),
        ])
        items = pilot.checklist(["w1"])         # no task files: Claude Code deleted them
        self.assertEqual([(i["subject"], i["status"]) for i in items], [("write test", "completed")])

    def test_a_continued_session_numbers_its_new_items_from_one_again(self):
        self.transcript("w2", [      # a `say` copy: the earlier session's calls, then its own store from #1
            tool_use("t1", "TaskCreate", {"subject": "step 1"}), tool_result("t1", "Task #1 created"),
            tool_use("t2", "TaskCreate", {"subject": "step 2"}), tool_result("t2", "Task #2 created"),
            tool_use("t3", "TaskUpdate", {"taskId": "1", "status": "completed"}), tool_result("t3", "Updated task #1 status"),
            tool_use("t4", "TaskCreate", {"subject": "R1 B1 fix"}), tool_result("t4", "Task #1 created"),
            tool_use("t5", "TaskUpdate", {"taskId": "1", "status": "in_progress"}), tool_result("t5", "Updated task #1 status"),
        ])
        self.assertEqual([(i["subject"], i["status"]) for i in pilot.checklist(["w2"])],
                         [("step 1", "completed"), ("step 2", "pending"), ("R1 B1 fix", "in_progress")])

    def test_who_refused_a_call(self):
        cases = [("denied by the Claude Code auto mode classifier", "auto mode"),
                 ("PreToolUse:Bash hook error: no", "a hook"),
                 ("<tool_use_error>Blocked: outside the worktree", "Claude Code"),
                 ("Permission to use Bash has been denied", "a deny rule")]
        for text, who in cases:
            ev = pilot.events_of(tool_result("t", text, error=True))
            self.assertEqual(ev[0]["denied"], who, text)
        self.assertIsNone(pilot.events_of(tool_result("t", "denied by the Claude Code auto mode classifier"))[0]["denied"],
                          "only an error result is a refusal")

    def test_a_refusal_by_the_wiring_is_a_gate(self):
        cases = [("pilot budget: this worker has spent 10,000,000 of its 10,000,000 weighted tokens.", "pilot's budget", False),
                 ("pilot: don't ask in the terminal. A prompt there blocks you", "pilot's ask hook", True),
                 ("PreToolUse:Bash hook error: pilot budget: this worker has spent 10,000,000", "pilot's budget", False),
                 ("PreToolUse:Bash hook error: SAM-REPLY-GATE: reply first", "the reply gate", True),
                 ("This session is isolated in the worktree C:/repo/.claude/worktrees/w-x", "Claude Code", False),
                 ("denied by the Claude Code auto mode classifier", "auto mode", False)]
        for text, who, gate in cases:
            ev = pilot.events_of(tool_result("t", text, error=True))[0]
            self.assertEqual((ev["denied"], ev.get("gate", False)), (who, gate), text)
        self.assertNotIn("gate", pilot.events_of(tool_result("t", "pilot: don't ask in the terminal"))[0],
                         "only an error result is refused")

    def test_an_agent_call_links_the_subagent_it_started(self):
        main = self.transcript("s1", [
            tool_use("toolu_a", "Agent", {"description": "Co-pilot: split legs", "subagent_type": "pilot:copilot", "prompt": "?"}),
            tool_use("toolu_b", "Agent", {"description": "not started yet", "prompt": "?"}),
            tool_use("toolu_c", "Bash", {"command": "ls"}),
            tool_result("toolu_a", "Async agent launched", toolUseResult={"agentId": "a421b8957fc08c300"})])
        staff = main.parent / "s1" / "subagents"
        staff.mkdir(parents=True)
        (staff / "agent-a421b8957fc08c300.meta.json").write_text(json.dumps(
            {"agentType": "pilot:copilot", "description": "Co-pilot: split legs", "toolUseId": "toolu_a"}), encoding="utf-8")
        (staff / "agent-a421b8957fc08c300.jsonl").write_text("", encoding="utf-8")
        events = pilot.link_spawns(pilot.read_events(main)[0], staff)
        calls = {e["id"]: e.get("agentId") for e in events if e["kind"] == "tool"}
        self.assertEqual(calls, {"toolu_a": "a421b8957fc08c300", "toolu_b": None, "toolu_c": None})
        self.assertEqual([e["agentId"] for e in events if e["kind"] == "result"], ["a421b8957fc08c300"])
        self.assertEqual(pilot.link_spawns([], staff / "missing"), [])

    def test_events_of_a_turn(self):
        said = pilot.events_of({"type": "assistant", "message": {"content": [
            {"type": "text", "text": "DONE: fixed"}, {"type": "tool_use", "id": "t", "name": "Bash", "input": {"command": "ls"}}]}})
        self.assertEqual([e["kind"] for e in said], ["said", "tool"])
        msg = pilot.events_of({"type": "user", "message": {"content":
            '<cross-session-message from-name="WA1 pilot">Another Claude session sent a message: go on</cross-session-message>'}})
        self.assertEqual((msg[0]["kind"], msg[0]["sender"], msg[0]["text"]), ("message", "WA1 pilot", "go on"))

    def test_a_subagent_hand_back_shows_only_its_report(self):
        c = ('Another Claude session sent a message:\n<agent-message from="a286c796cdd8c0752">\n[Subagent hand-back] The text '
             'below is the final report of a subagent this session delegated to. The report follows:\n  Agree: fold leg 0 '
             'into leg B.\n  \n  - commit the capture tool first\n</agent-message>\n\nThat "other Claude session" is an agent '
             'working inside this same session.')
        ev = pilot.events_of({"type": "user", "message": {"content": c}})
        self.assertEqual([(e["kind"], e["agentId"]) for e in ev], [("handback", "a286c796cdd8c0752")])
        self.assertEqual(ev[0]["text"], "Agree: fold leg 0 into leg B.\n\n- commit the capture tool first")

    def test_report_head_word_and_outside_line(self):
        self.assertEqual(pilot.head_word("**DONE** all green"), "DONE")
        self.assertEqual(pilot.head_word("BLOCKEDish"), None)
        self.assertIsNone(pilot.head_word("I think it is done"))
        rep = "DONE\n\nOutside the worktree:\n- a WSL clone at ~/src/x\n- scratch/goldens\nThanks"
        self.assertEqual(pilot.outside_line(rep), "- a WSL clone at ~/src/x\n- scratch/goldens")
        self.assertEqual(pilot.outside_line("DONE\n**Outside the worktree:** nothing"), "nothing")
        self.assertIsNone(pilot.outside_line("DONE"))


NOW = time.mktime((2026, 10, 14, 12, 0, 0, 0, 0, -1))     # a Wednesday, noon local


class RefuelTests(Case):
    def limits(self, five, week, age=60, five_reset=3600, week_reset=4 * 86400):
        pilot.write_json(pilot.PROFILE / "rate-limits.json", {"written_at": NOW - age, "rate_limits": {
            "five_hour": {"used_percentage": five, "resets_at": NOW + five_reset},
            "seven_day": {"used_percentage": week, "resets_at": NOW + week_reset}}})

    def test_no_figures_or_old_figures_hold_every_refuel(self):
        (pilot.PROFILE / "rate-limits.json").unlink()
        self.assertEqual(pilot.headroom(NOW), (False, ["no usage figures: the status line writes them"]))
        self.limits(10, 10, age=31 * 60)
        self.assertFalse(pilot.headroom(NOW)[0])

    def test_headroom(self):
        self.limits(20, 30)
        self.assertTrue(pilot.headroom(NOW)[0])
        self.limits(85, 30)
        self.assertFalse(pilot.headroom(NOW)[0], "past maxUsedPct")

    def test_a_five_hour_window_running_dry_before_its_reset_holds(self):
        self.limits(70, 30, five_reset=1800)     # 70% in 4.5 h: dry in about 2 h, the reset is in 30 min
        self.assertTrue(pilot.headroom(NOW)[0])
        self.limits(70, 30, five_reset=4 * 3600)  # 70% in 1 h: dry in under half an hour
        ok, why = pilot.headroom(NOW)
        self.assertFalse(ok)
        self.assertIn("before its reset", why[0])

    def test_the_week_counts_as_running_dry_only_late_and_close(self):
        self.limits(10, 40, week_reset=5 * 86400)      # fast, but under 50% used
        ok, why = pilot.headroom(NOW)
        self.assertTrue(ok)
        self.assertIn("under 50% used", why[1])
        self.limits(10, 75, week_reset=4 * 86400)       # 75% in three days: dry in about a day, well before the reset
        self.assertFalse(pilot.headroom(NOW)[0])

    def refuel(self, tokens, reason="the API and its tests are left, about 3M each"):
        return pilot.refuel("fix", tokens, reason)

    def setUp(self):
        super().setUp()
        self.plan("s1", PLAN)
        self.worker("fix", plan="s1")
        self.limits(20, 30)
        self._now, pilot.time.time = pilot.time.time, lambda: NOW
        self.asked = []
        self._tower, pilot.watchtower = pilot.watchtower, lambda w, t, r, a: (self.asked.append(t) or (True, "progress justifies it"))

    def tearDown(self):
        pilot.time.time, pilot.watchtower = self._now, self._tower
        super().tearDown()

    def test_within_the_gates_the_watchtower_decides(self):
        self.assertEqual(self.refuel(16_000_000), (True, "progress justifies it"))
        self.assertEqual(pilot.load("fix")["budget"], 16_000_000)
        self.assertIn("Watchtower approved: refuel fix to 16,000,000", self.log_of("s1")[-1])

    def test_gates_hold_without_asking_the_watchtower(self):
        approved, why = self.refuel(25_000_000)
        self.assertFalse(approved)
        self.assertIn("past 2 times", why)
        pilot.set_refuel_mode("manual")
        approved, why = self.refuel(12_000_000)
        self.assertFalse(approved)
        self.assertIn("manual", why)
        self.assertEqual(self.asked, [])
        self.assertEqual(pilot.load("fix")["budget"], 10_000_000)
        self.assertIn("Watchtower sent to the tower", self.log_of("s1")[-1])

    def test_a_refuel_needs_a_reason_and_a_raise(self):
        with self.assertRaises(ValueError):
            self.refuel(16_000_000, reason="more please")
        with self.assertRaises(ValueError):
            self.refuel(9_000_000)

    def test_the_dashboard_gets_each_legs_first_budget_for_its_refuel_button(self):
        self.assertEqual(pilot.worker_status(pilot.load("fix"), [])["budget0"], 10_000_000)
        self.config(budget=8_000_000)               # a worker from before budget0, since raised: the config's
        old = self.worker("old", budget=16_000_000, budget0=None)
        self.assertEqual(pilot.worker_status(old, [])["budget0"], 8_000_000)


class HookTests(Case):
    def test_budget_checkpoint(self):
        self.worker("fix", budget=1000)
        self.transcript("sess-fix", [usage("m1", inp=900)])
        call = {"session_id": "sess-fix", "tool_name": "Bash"}
        self.assertEqual(self.hook(pilot.cmd_hook_budget, call), "", "under budget, it passes")
        self.transcript("sess-fix", [usage("m1", inp=900), usage("m2", out=40)])
        out = json.loads(self.hook(pilot.cmd_hook_budget, call))["hookSpecificOutput"]
        self.assertEqual(out["permissionDecision"], "deny")
        self.assertIn("1,100 of its 1,000", out["permissionDecisionReason"])
        self.assertEqual(self.hook(pilot.cmd_hook_budget, {**call, "tool_name": "SendMessage"}), "",
                         "SendMessage always passes, so the worker can report")
        self.assertEqual(self.hook(pilot.cmd_hook_budget, {**call, "session_id": "someone-else"}), "")

    def test_a_pilot_with_a_plan_is_refused_the_terminal_question(self):
        self.plan("s1", PLAN)
        q = {"questions": [{"question": "Push it?", "header": "Push"}]}
        out = self.hook(pilot.cmd_hook_ask, {"session_id": "s1", "hook_event_name": "PreToolUse", "tool_input": q})
        self.assertEqual(json.loads(out)["hookSpecificOutput"]["permissionDecision"], "deny")
        self.assertIn("refused, so the question goes in the waiting section: Push it?", self.log_of("s1")[-1])

    def test_without_a_plan_the_question_is_recorded_until_answered(self):
        q = {"questions": [{"question": "Push it?", "header": "Push"}]}
        self.assertEqual(self.hook(pilot.cmd_hook_ask, {"session_id": "s9", "hook_event_name": "PreToolUse", "tool_input": q}), "")
        self.assertTrue((pilot.ASKS / "s9.json").exists())
        self.hook(pilot.cmd_hook_ask, {"session_id": "s9", "hook_event_name": "PostToolUse", "tool_input": q,
                                       "tool_response": {"answers": {"Push it?": "Yes"}}})
        self.assertFalse((pilot.ASKS / "s9.json").exists())


class DaemonLockTests(Case):
    def lock(self, pid, ft):
        pilot.write_json(pilot.DAEMON_LOCK, {"pid": pid, "procStartFt": ft, "version": "2.1.296"})

    def test_no_lock_or_a_live_daemon_is_not_stale(self):
        self.assertIsNone(pilot.daemon_lock())
        ft, _ = pilot.process_identity(os.getpid())
        self.lock(os.getpid(), str(ft))
        self.assertIsNone(pilot.daemon_lock())

    def test_a_reused_pid_is_stale(self):
        self.lock(os.getpid(), "1")                  # this process, but not the one that took the lock
        stale = pilot.daemon_lock()
        self.assertIn("a different process", stale["why"])
        self.assertIn(Path(sys.executable).name.lower(), stale["why"].lower())

    def test_a_gone_daemon_is_stale(self):
        p = subprocess.Popen([sys.executable, "-c", "pass"])
        p.wait()
        self.lock(p.pid, "1")
        self.assertIn("is not running", pilot.daemon_lock()["why"])

    def test_start_refuses_and_names_the_lock(self):
        self.lock(os.getpid(), "1")
        with self.assertRaises(SystemExit) as e:
            pilot.cmd_start(types.SimpleNamespace(name="leg", after=None))
        self.assertIn("daemon lock", str(e.exception))
        self.assertTrue(pilot.DAEMON_LOCK.exists(), "pilot.py never removes it itself")


class RemoveTests(Case):
    def rm(self, *results):
        """Runs `rm fix` with `claude rm` answering each session in turn, and returns the calls it made."""
        calls, answers = [], list(results)

        def fake(*args, timeout=60, **_):
            calls.append((args, timeout))
            code, out = answers.pop(0)
            return types.SimpleNamespace(returncode=code, stdout=out, stderr="")
        real, pilot.claude = pilot.claude, fake
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                pilot.cmd_rm(types.SimpleNamespace(name="fix"))
        finally:
            pilot.claude = real
        return calls

    def setUp(self):
        super().setUp()
        self.worker("fix", worktree=str(pilot.STATE / "no-such-worktree"),
                    sessions=[{"id": "job-a", "sessionId": "sess-a"}, {"id": "job-b", "sessionId": "sess-b"}])

    def test_the_copies_go_first_and_nothing_times_out(self):
        calls = self.rm((0, ""), (0, ""))
        self.assertEqual([c[0] for c in calls], [("rm", "job-b"), ("rm", "job-a")])
        self.assertEqual({c[1] for c in calls}, {None}, "a 6 GB worktree took six and a half minutes to delete")
        self.assertIsNone(pilot.load("fix"))
        self.assertEqual(len(list(pilot.ARCHIVE.glob("fix-*.json"))), 1)

    def test_sessions_already_removed_by_hand_still_archive(self):
        self.rm((1, "No job matching 'job-b'"), (1, "no such session"))
        self.assertIsNone(pilot.load("fix"))

    def test_a_failure_keeps_the_record(self):
        with self.assertRaises(SystemExit):
            self.rm((0, ""), (1, "permission denied"))
        self.assertIsNotNone(pilot.load("fix"))


class RunawayKitTests(Case):
    def test_every_hook_the_worker_plugin_wires_is_in_the_repo(self):
        hooks = pilot.HERE / "plugins" / "worker" / "hooks"
        wired = set(re.findall(r'kit\.sh\\" (\w+\.sh)', (hooks / "hooks.json").read_text(encoding="utf-8")))
        self.assertEqual(len(wired), 5, wired)
        for name in wired:
            self.assertTrue((hooks / "runaway" / name).is_file(), name)
            self.assertNotIn(b"\r", (hooks / "runaway" / name).read_bytes(), f"{name}: Git Bash fails on a CR")


class PullRequestTests(Case):
    def test_a_removed_legs_deleted_branch_is_read_from_the_tip_its_record_kept(self):
        repo = ROOT / "tip-repo"
        shutil.rmtree(repo, ignore_errors=True)
        git = lambda *a: subprocess.run(["git", "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@t", *a],
                                        check=True, capture_output=True, text=True).stdout.strip()
        repo.mkdir()
        git("init", "-q", "-b", "main")
        git("commit", "-q", "--allow-empty", "-m", "base")
        git("checkout", "-q", "-b", "worktree-w-build")
        git("commit", "-q", "--allow-empty", "-m", "the fix")
        commits = git("log", "--oneline", "main..HEAD")
        git("checkout", "-q", "main")
        git("branch", "-q", "-D", "worktree-w-build")
        self.config(repos={"web-app": str(repo)})
        w = {"repo": "web-app", "branch": "worktree-w-build", "base": "main", "worktree": str(repo / "gone"), "started": 0,
             "final": {"commits": commits}}
        g = pilot.collateral_now(w)
        self.assertTrue(g["gone"])
        self.assertEqual((g["ahead"], g["prs"]), (1, []), "on no remote: no PR, and one commit past its base")
        self.assertEqual(pilot.collateral_now({**w, "final": {"commits": ""}}).get("ahead"), None, "no tip kept: nothing to read")


    def test_checks_fold_to_one_word(self):
        pr = lambda **kw: pilot.pr_view({"number": 1, "state": "OPEN", **kw})
        self.assertEqual(pr()["ci"], "none")
        self.assertEqual(pr(statusCheckRollup=[{"status": "COMPLETED", "conclusion": "SUCCESS"},
                                               {"status": "COMPLETED", "conclusion": "SKIPPED"}])["ci"], "passing")
        self.assertEqual(pr(statusCheckRollup=[{"status": "IN_PROGRESS"}, {"conclusion": "SUCCESS"}])["ci"], "running")
        self.assertEqual(pr(statusCheckRollup=[{"status": "IN_PROGRESS"}, {"conclusion": "FAILURE"}])["ci"], "failing")
        self.assertEqual(pr(isDraft=True)["state"], "DRAFT")
        self.assertEqual(pr(isDraft=True, state="MERGED")["state"], "MERGED")

    def test_a_required_review_means_the_team_reviews_it(self):
        pr = lambda repo, decision: pilot.pr_view({"number": 1, "state": "OPEN", "reviewDecision": decision}, repo)["reviewer"]
        self.assertEqual(pr("drawboard-pdf-sdk", "REVIEW_REQUIRED"), "team")
        self.assertEqual(pr("drawboard-pdf-sdk", "APPROVED"), "team")
        self.assertEqual(pr("tianwen", ""), "self")             # no branch rule: the pilot and co-pilot review it
        self.config(review={"tianwen": "team", "web-app": "self"})
        self.assertEqual(pr("tianwen", ""), "team")             # a team that reviews by convention
        self.assertEqual(pr("web-app", "REVIEW_REQUIRED"), "self")

    def test_a_plan_names_prs_by_repo_and_number(self):
        self.config(repos={"DIR.Lib": str(ROOT), "tianwen": str(ROOT)})
        asked = []
        self._gh, pilot.gh_json = pilot.gh_json, lambda root, *a: asked.append(a[2]) or {"number": int(a[2]), "state": "MERGED"}
        try:
            text = "Merge DIR.Lib #109 first, then dir.lib PR #110; tianwen #5. Not #7, not foo.DIR.Lib #8."
            for _ in range(50):
                got = pilot.mentioned_prs(text)
                if len(got) == 3:
                    break
                time.sleep(0.05)
        finally:
            pilot.gh_json = self._gh
            pilot._prs.clear()
        self.assertEqual(sorted((p["repo"], p["number"]) for p in got), [("DIR.Lib", 109), ("DIR.Lib", 110), ("tianwen", 5)])
        self.assertEqual(sorted(asked), ["109", "110", "5"])


class TrackTests(Case):
    def test_leg_states(self):
        two = [{"status": "completed"}, {"status": "in_progress"}]
        cases = [(None, ("planned", "planned")),
                 ({"state": "working"}, ("working", "flying")),
                 ({"state": "blocked"}, ("warn", "waiting on you")),
                 ({"state": "done", "reportHead": "DONE", "checklist": two}, ("warn", "done, 1 item open")),
                 ({"state": "done", "reportHead": "DONE", "checklist": two[:1]}, ("good", "landed")),
                 ({"state": "done", "reportHead": "BLOCKED"}, ("warn", "blocked")),
                 ({"final": {"reportHead": "FAILED"}}, ("bad", "failed")),
                 ({"state": "done"}, ("planned", "turn ended"))]
        for w, want in cases:
            self.assertEqual(pilot.leg_state(w), want, w)

    def test_page_shows_waiting_legs_and_escapes_what_the_plan_says(self):
        self.plan("s1", PLAN.replace("Write the design note", "Write <script>alert(1)</script>"))
        self.worker("fix", plan="s1")
        self.rows = [{"id": "job-fix", "sessionId": "sess-fix", "state": "working", "status": "busy", "pid": 1}]
        self.transcript("sess-fix", [usage("m1", out=200_000)])
        pilot.write_json(pilot.ARCHIVE / "design-1.json", {"name": "design", "plan": "s1", "archivedAt": 1,
                         "final": {"reportHead": "DONE", "checklist": [{"status": "completed", "subject": "x"}]}})
        page = pilot.track_html("s1")
        self.assertIn("Flight WA1 · Export fixes", page)
        self.assertIn("Waiting on you: 2 items", page)
        self.assertIn("landed · removed", page)
        self.assertIn(">flying<", page)
        self.assertIn("spend 1.0M of 10M", page)
        self.assertNotIn("<script>", page)
        self.assertIn("&lt;script&gt;", page)

    def test_track_records_its_url_once(self):
        self.plan("s1", PLAN)
        args = lambda url=None, out=None: types.SimpleNamespace(session="s1", url=url, out=out)
        with contextlib.redirect_stdout(io.StringIO()) as out:
            pilot.cmd_track(args("https://claude.ai/code/artifact/a"))
            pilot.cmd_track(args("https://claude.ai/code/artifact/b"))
            pilot.cmd_track(args(out=str(ROOT / "track.html")))
        text = pilot.plan_path("s1").read_text(encoding="utf-8")
        self.assertEqual(text.count("Tracking:"), 1)
        self.assertEqual(pilot.parse_plan(text)["tracking"], "https://claude.ai/code/artifact/b")
        self.assertIn("publish it to https://claude.ai/code/artifact/b", out.getvalue())
        self.assertTrue((ROOT / "track.html").exists())

    def test_markdown(self):
        self.assertEqual(pilot._md("- **a** `b` <x>\n  wrapped\n\n### H\npara"),
                         "<ul><li><b>a</b> <code>b</code> &lt;x&gt; wrapped</li></ul>\n<h3>H</h3>\n<p>para</p>")


LANES = """# Flight plan: Lanes

Flight: WA2

Objective: Fork, join and an alternate.

Pilot session: s1

## Route

| Leg | Goal | After | Done when | Alt of |
|---|---|---|---|---|
| scaffold | Lay out the module | - | builds | |
| api | Write the API | scaffold | tests pass | - |
| ui | Write the UI | scaffold | tests pass | |
| api-b | The API another way | scaffold | tests pass | `api` |
| ship | Wire them up | api, ui | e2e passes | |

## Log

- 2026-10-13 09:00 Plan opened.
"""


class AlternateTests(Case):
    """Lanes from After (a fork and a join), the optional `Alt of` column, an alternate apart from the route
    until it is taken, and `start --divert`."""

    def archive(self, leg, head="DONE", **kw):
        pilot.write_json(pilot.ARCHIVE / f"{leg}-1.json", {"name": leg, "plan": "s1", "archivedAt": 1, "started": 1000.0, **kw,
                         "final": {"reportHead": head, "checklist": [{"status": "completed", "subject": "x"}]}})

    def states(self):
        legs = pilot.parse_plan(pilot.plan_path("s1").read_text(encoding="utf-8"))["legs"]
        return pilot.flight_legs("s1", legs, [pilot.worker_status(w, self.rows) for w in pilot.registered()], pilot.archived())

    def start(self, name, divert=None, launch=False):
        """`pilot.py start` from pilot session s1. With `launch`, a fake `claude --bg` lets it take off."""
        a = types.SimpleNamespace(name=name, after=None, divert=divert, repo="r", brief=str(ROOT / "brief.md"),
                                  model=None, effort=None, budget=None, pilot="pilot-x")
        with contextlib.ExitStack() as st:
            st.enter_context(unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": "s1"}))
            if not launch:
                pilot.write_json(pilot.DAEMON_LOCK, {"pid": os.getpid(), "procStartFt": "1"})   # stops it past the checks
                with self.assertRaises(SystemExit) as e:
                    pilot.cmd_start(a)
                pilot.DAEMON_LOCK.unlink()
                return str(e.exception)
            (ROOT / "repo" / ".git").mkdir(parents=True, exist_ok=True)
            (ROOT / "brief.md").write_text("Do it.", encoding="utf-8")
            self.config(repos={"r": str(ROOT / "repo")})
            st.enter_context(unittest.mock.patch.object(pilot, "claude", lambda *x, **k: types.SimpleNamespace(
                returncode=0, stdout="backgrounded · deadbeef", stderr="")))
            st.enter_context(unittest.mock.patch.object(pilot, "session_of", lambda j: f"sess-{name}"))
            st.enter_context(unittest.mock.patch.object(pilot, "base_branch", lambda r: "main"))
            st.enter_context(contextlib.redirect_stdout(io.StringIO()))
            pilot.cmd_start(a)
            return pilot.load(name)

    def test_the_alt_of_column_is_optional(self):
        self.assertEqual([l["altOf"] for l in pilot.parse_plan(PLAN)["legs"]], ["", ""], "a 4-column route has none")
        legs = pilot.parse_plan(LANES)["legs"]
        self.assertEqual([(l["leg"], l["altOf"]) for l in legs],
                         [("scaffold", ""), ("api", ""), ("ui", ""), ("api-b", "api"), ("ship", "")])
        self.assertEqual(legs[4]["after"], ["api", "ui"])
        self.assertEqual(legs[4]["doneWhen"], "e2e passes")
        self.assertIn("| Leg | Goal | After | Done when | Alt of |", pilot.PLAN_TEMPLATE)

    def test_lanes_fork_and_join_by_depth_and_an_alternate_takes_its_primarys(self):
        d = pilot.route_depths(pilot.parse_plan(LANES)["legs"])
        self.assertEqual(d, {"scaffold": 0, "api": 1, "ui": 1, "ship": 2, "api-b": 1})
        # Lanes need not share their After; a leg after an alternate waits, for depth, on its primary.
        legs = [{"leg": "a", "after": [], "altOf": ""}, {"leg": "b", "after": [], "altOf": ""},
                {"leg": "c", "after": ["a"], "altOf": ""}, {"leg": "a2", "after": [], "altOf": "a"},
                {"leg": "d", "after": ["c", "b", "a2"], "altOf": ""}, {"leg": "x", "after": ["y"], "altOf": ""},
                {"leg": "y", "after": ["x"], "altOf": ""}]
        d = pilot.route_depths(legs)
        self.assertEqual((d["a"], d["b"], d["c"], d["a2"], d["d"]), (0, 0, 1, 0, 2))
        self.assertIsInstance(d["x"], int, "a cycle does not loop")

    def test_an_unflown_alternate_never_keeps_the_flight_from_landing(self):
        self.plan("s1", LANES)
        for leg in ("scaffold", "api", "ui", "ship"):
            self.archive(leg)
        st = self.states()
        self.assertEqual([(s["leg"], s["taken"]) for s in st if s["altOf"]], [("api-b", False)])
        self.assertEqual(pilot.flight_landed(st), (True, []))
        self.assertEqual(len(pilot.flown(st)), 4)
        times = pilot.flight_times("s1", pilot.parse_plan(LANES)["legs"], [], pilot.archived())
        self.assertEqual(times["basis"], "all 4 legs landed")
        self.assertIn("Landed: all 4 legs", pilot.track_html("s1"))
        self.assertIn("alternate, not taken", pilot.track_html("s1"))

    def test_a_failed_primary_with_a_landed_alternate_counts_as_done(self):
        self.plan("s1", LANES)
        for leg in ("scaffold", "ui", "ship"):
            self.archive(leg)
        self.archive("api", head="FAILED")
        landed, left = pilot.flight_landed(self.states())
        self.assertFalse(landed)
        self.assertEqual(left, ["api: failed"])
        self.archive("api-b", divertedFrom="api", divertReason="the old client cannot be fixed")
        st = self.states()
        self.assertEqual(pilot.flight_landed(st), (True, []))
        self.assertEqual([s["leg"] for s in pilot.flown(st)], ["scaffold", "api-b", "ui", "ship"])
        page = pilot.track_html("s1")
        self.assertIn("diverted from api: the old client cannot be fixed", page)
        self.assertIn("Landed: all 4 legs", page)

    def test_an_alternate_removed_before_it_landed_is_no_longer_taken(self):
        self.plan("s1", LANES)
        self.archive("api", head="FAILED")
        self.archive("api-b", head="FAILED", divertedFrom="api")
        st = {s["leg"]: s for s in self.states()}
        self.assertFalse(st["api-b"]["taken"])
        self.assertIn("api", [s["leg"] for s in pilot.flown(list(st.values()))])

    def test_start_takes_an_alternate_only_on_a_diversion(self):
        self.plan("s1", LANES)
        self.assertIn("api-b is an alternate for api; take it only on a diversion", self.start("api-b"))
        self.assertIn("api is no alternate on the route", self.start("api", divert="why not"))
        self.worker("api", plan="s1")
        self.rows = [{"id": "job-api", "sessionId": "sess-api", "state": "working", "status": "busy", "pid": 1}]
        self.assertIn("cannot divert api to api-b: api is flying", self.start("api-b", divert="too slow"))
        self.rows = [{"id": "job-api", "sessionId": "sess-api", "state": "blocked", "status": "idle", "pid": 1}]
        self.assertIn("cannot divert api to api-b: api is waiting on you", self.start("api-b", divert="too slow"))
        self.rows = [{"id": "job-api", "sessionId": "sess-api", "state": "done", "status": "idle", "pid": 1}]
        self.assertIn("daemon lock", self.start("api-b", divert="stopped"), "stopped: past to the next check")
        (pilot.WORKERS / "api.json").unlink()
        self.assertIn("daemon lock", self.start("api-b", divert="never flew"), "planned: allowed")
        self.archive("api", head="FAILED")
        w = self.start("api-b", divert="the old client cannot be fixed", launch=True)
        self.assertEqual((w["divertedFrom"], w["divertReason"]), ("api", "the old client cannot be fixed"))
        self.assertIn("Leg api diverted to api-b: the old client cannot be fixed", self.log_of("s1")[-2])
        self.assertIn("Leg api-b took off", self.log_of("s1")[-1])
        self.assertTrue({s["leg"]: s for s in self.states()}["api-b"]["taken"])

    def test_start_refuses_a_primary_while_its_alternate_has_a_worker(self):
        self.plan("s1", LANES)
        self.worker("api-b", plan="s1", divertedFrom="api", divertReason="x")
        self.assertIn("api was diverted to api-b; `pilot.py rm api-b` first to undo the diversion", self.start("api"))
        (pilot.WORKERS / "api-b.json").unlink()                     # rm undoes it
        self.assertIn("daemon lock", self.start("api"))


if __name__ == "__main__":
    unittest.main()
