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

    def last_box(self):
        """The black box's last entry: its text and the actor blackbox() took from it."""
        e = json.loads(pilot.BLACKBOX.read_text(encoding="utf-8").splitlines()[-1])
        return e["text"], e["actor"]


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

    def test_a_pilot_opening_its_plan_registers_its_repo(self):
        repo = ROOT / "graph-app"
        repo.mkdir(exist_ok=True)
        subprocess.run(["git", "init", "-q", str(repo)], check=True)
        self.config(repos={})
        cwd, env = os.getcwd(), dict(os.environ)
        os.chdir(repo)
        try:
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                pilot.cmd_plan(types.SimpleNamespace(session="s3", title="from elsewhere"))
                self.assertEqual(pilot.config()["repos"], {}, "--session may be run from anywhere")
                os.environ["CLAUDE_CODE_SESSION_ID"] = "s4"
                pilot.cmd_plan(types.SimpleNamespace(session=None, title="Graphs"))
        finally:
            os.chdir(cwd)
            os.environ.clear()
            os.environ.update(env)
        self.assertEqual(list(pilot.config()["repos"]), ["graph-app"])
        self.assertTrue(os.path.samefile(pilot.config()["repos"]["graph-app"], repo))

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

    def test_the_glossary_rewording_keeps_every_log_lines_actor(self):
        # blackbox() takes an actor from the start of a line's text (ACTORS); terms-rename reworded these lines'
        # tails, never their prefixes, so each keeps the actor it had. The refuel switch and the autopilot's pass
        # their own actor (controller) besides; the refuel test pins the switch's.
        cases = [("Watchtower approved: refuel fix to 16,000,000. ok. Asked because: x", "watchtower"),
                 ("Watchtower sent to the controller: refuel fix to 12,000,000. manual. Asked because: x", "watchtower"),
                 ("Review round 2 of leg-a (PR #7 at 11111111): 1 bug(s) left after 2 rounds that found bugs: to the controller.", "pilot.py"),
                 ("Refuelling set to manual: every refuel goes to the controller", "pilot.py")]
        for text, actor in cases:
            pilot.blackbox(text)
            self.assertEqual(self.last_box(), (text, actor))
        pilot.blackbox("Autopilot switched on: it decides the controller's items until switched off", actor="controller")
        self.assertEqual(self.last_box()[1], "controller")


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
        self.assertIn("TIA2 (Theme tokens): 0 of 1 legs done; still open: build: planned", msg)
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
        self.assertEqual(pilot.flight_landed(leg({"ahead": 2, "tip": "abc123", "prs": [{"number": 7, "state": "MERGED", "head": "abc123"}]})), (True, []))
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
        self.git = {"ahead": 3, "tip": "abc123", "prs": [{"number": 7, "state": "MERGED", "head": "abc123"}], "remotes": ["origin/feat/x"]}
        self.assertIsNone(pilot.connection_hold("s1"))

    def test_the_dashboard_gets_each_connection_and_whether_it_landed(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nConnects: TIA2, TIA9"))
        self.plan("s2", CONNECTED)
        p = next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s1")
        self.assertEqual([(c["flight"], c["known"], c["landed"]) for c in p["connections"]], [("TIA2", True, False), ("TIA9", False, False)])
        self.landed()
        p = next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s1")
        self.assertEqual((p["connections"][0]["landed"], p["connections"][0]["state"]), (True, "landed"))

    def test_the_dashboard_gets_each_legs_landed_flag(self):
        self.plan("s2", CONNECTED.replace("| build | Build the tokens | - | tests pass |",
                                          "| build | Build the tokens | - | tests pass |\n| ship | Ship them | build | shipped |"))
        self.landed()
        flags = lambda: next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s2")["landed"]
        merged = {"number": 7, "state": "MERGED", "head": "abc123"}
        cases = [({"ahead": 2, "tip": "abc123", "prs": [merged], "remotes": []}, {"landed": True, "why": None, "stackedOn": []}),
                 ({"ahead": 0, "prs": [], "remotes": []}, {"landed": True, "why": None, "stackedOn": []}),
                 ({"ahead": 2, "tip": "abc123", "prs": [{"number": 7, "state": "OPEN", "head": "abc123"}], "remotes": []},
                  {"landed": False, "why": "on approach", "stackedOn": []}),
                 ({"ahead": 2, "prs": [], "remotes": []}, {"landed": False, "why": "2 commits on no merged pull request", "stackedOn": []}),
                 ({"ahead": 2, "prs": None, "remotes": ["origin/feat/x"]}, {"landed": False, "why": "its pull requests not read yet", "stackedOn": []})]
        for git, want in cases:
            self.git = git
            got = flags()
            self.assertEqual(got["build"], want, git)
            self.assertEqual(got["ship"], {"landed": False, "why": "planned", "stackedOn": []}, "a leg not flown has not landed")

    def test_a_leg_flying_on_legs_not_landed_is_stacked_on_them(self):
        legs = [{"leg": "audit", "after": [], "altOf": ""}, {"leg": "rename", "after": ["audit"], "altOf": ""},
                {"leg": "strip", "after": ["rename", "audit"], "altOf": ""}, {"leg": "ship", "after": ["strip"], "altOf": ""}]
        done = lambda leg, git: {"leg": leg, "w": {"reportHead": "DONE"}, "cls": "good", "label": "done", "git": git, "altOf": "", "taken": False}
        flying = lambda leg: {"leg": leg, "w": {"state": "working"}, "cls": "working", "label": "flying", "git": None, "altOf": "", "taken": False}
        planned = lambda leg: {"leg": leg, "w": None, "cls": "planned", "label": "planned", "git": None, "altOf": "", "taken": False}
        unmerged, merged = {"ahead": 2, "prs": []}, {"ahead": 0, "prs": []}
        # PIL2's: audit flying again (a rebase) and rename done, not landed, while strip flies on both.
        got = pilot.leg_landings(legs, [flying("audit"), done("rename", unmerged), flying("strip"), planned("ship")])
        self.assertEqual({k: v["stackedOn"] for k, v in got.items()}, {"audit": [], "rename": ["audit"], "strip": ["rename", "audit"], "ship": []})
        # Once audit has landed, strip is stacked on rename alone; once rename has too, on nothing.
        got = pilot.leg_landings(legs, [done("audit", merged), done("rename", unmerged), flying("strip"), planned("ship")])
        self.assertEqual(got["strip"]["stackedOn"], ["rename"])
        self.assertEqual(got["rename"]["stackedOn"], [], "a leg on landed legs is not stacked")
        got = pilot.leg_landings(legs, [done("audit", merged), done("rename", merged), flying("strip"), planned("ship")])
        self.assertEqual(got["strip"]["stackedOn"], [])
        # A leg after one not flown yet is not stacked: it waits, it builds on nothing.
        got = pilot.leg_landings(legs, [planned("audit"), planned("rename"), flying("strip"), planned("ship")])
        self.assertEqual(got["strip"]["stackedOn"], [])
        # An After naming a diverted primary is stacked on the alternate taken for it.
        alt = [*legs, {"leg": "rename-b", "after": ["audit"], "altOf": "rename"}]
        st = [done("audit", merged), {**planned("rename"), "cls": "bad", "label": "failed", "w": {}}, flying("strip"), planned("ship"),
              {**done("rename-b", unmerged), "altOf": "rename", "taken": True}]
        self.assertEqual(pilot.leg_landings(alt, st)["strip"]["stackedOn"], ["rename-b"])

    def test_the_tracking_page_tells_done_from_landed(self):
        self.plan("s2", CONNECTED)
        self.landed()
        self.git = {"ahead": 2, "tip": "abc123", "prs": [], "remotes": [], "unpushed": 2, "dirty": 0}     # DONE, unpushed
        page = pilot.track_html("s2")
        self.assertIn('<span class="chip done">done, not landed · removed</span>', page)
        self.assertIn('<div class="sub">not landed: 2 commits on no merged pull request</div>', page)
        self.assertIn("All 1 legs done; not landed: build: 2 commits on no merged pull request", page)
        self.git.update(prs=[{"number": 7, "state": "MERGED", "head": "abc123"}], remotes=["origin/feat/x"], unpushed=0)
        page = pilot.track_html("s2")
        self.assertIn('<span class="chip good">landed · removed</span>', page)
        self.assertNotIn("not landed", page)
        self.assertIn("Landed: all 1 legs", page)


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


class FlightArchiveTests(Case):
    """The controller's archive of whole flights, in archived-flights.json: the plan stays where it is, off the
    Overview and the order, on History, until a restore puts it back. Not the worker archive of `pilot rm`."""

    def setUp(self):
        super().setUp()
        now = time.time()
        for i, (sid, flight) in enumerate((("a", "WA1"), ("b", "WA2"), ("c", "WA3"))):
            text = PLAN.replace("Export fixes", f"Flight {sid}").replace("Flight: WA1", f"Flight: {flight}").replace("Pilot session: s1", f"Pilot session: {sid}")
            os.utime(self.plan(sid, text), (now - i * 60, now - i * 60))
        pilot.write_json(pilot.PRIORITY, ["c", "b", "a"])

    def order(self):
        return [p["sessionId"] for p in pilot.plans_view([], [])]

    def cli(self, flight, restore=False, force=False):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            pilot.cmd_archive(types.SimpleNamespace(flight=flight, restore=restore, force=force))
        return out.getvalue()

    def flying(self, leg="fix", plan="b"):
        self.worker(leg, plan=plan)
        self.rows = [{"id": f"job-{leg}", "sessionId": f"sess-{leg}", "state": "working", "status": "busy"}]

    def test_archive_keeps_the_plan_and_takes_it_off_the_overview_and_the_order(self):
        before = pilot.plan_path("b").read_text(encoding="utf-8")
        out = self.cli("WA2")
        self.assertIn("WA2 (Flight b) archived", out)
        self.assertEqual(self.order(), ["c", "a"])
        self.assertEqual(pilot.priority(), ["c", "a"])
        text = pilot.plan_path("b").read_text(encoding="utf-8")
        self.assertTrue(text.startswith(before), "the plan is kept whole, only a log line added")
        self.assertEqual(pilot.parse_plan(text)["legs"][0]["leg"], "design")
        self.assertIn("Archived by the controller", self.log_of("b")[-1])
        bb = [json.loads(l) for l in pilot.BLACKBOX.read_text(encoding="utf-8").splitlines()]
        self.assertEqual((bb[-1]["text"], bb[-1]["actor"], bb[-1]["flight"], bb[-1]["plan"]), ("Archived by the controller", "controller", "WA2", "b"))
        hist = pilot.flight_history([])
        self.assertEqual([(h["sessionId"], h["flight"], h["title"]) for h in hist], [("b", "WA2", "Flight b")])
        self.assertEqual([(l["leg"], l["label"]) for l in hist[0]["legs"]], [("design", "planned"), ("fix", "planned")])
        self.assertIn("times", hist[0])
        with unittest.mock.patch.object(pilot, "flight_history", lambda w, g=(): "fh"):
            snap = pilot.snapshot()
        self.assertEqual(snap["flightHistory"], "fh")
        self.assertNotIn("b", [p["sessionId"] for p in snap["plans"]])
        with self.assertRaises(SystemExit) as e:
            self.cli("WA2")
        self.assertEqual(str(e.exception), "pilot: WA2 (Flight b) is archived already")

    def test_restore_puts_it_back_in_its_old_place(self):
        self.cli("wa2")                                  # a flight number in any case, or its plan id
        pilot.set_priority(["a", "c"])                   # the controller reorders while it is away
        out = self.cli("b", restore=True)
        self.assertIn("WA2 (Flight b) restored", out)
        self.assertEqual(pilot.priority(), ["a", "b", "c"])
        self.assertEqual(self.order(), ["a", "b", "c"])
        self.assertEqual(pilot.flight_history([]), [])
        self.assertIn("Restored by the controller", self.log_of("b")[-1])
        with self.assertRaises(SystemExit) as e:
            self.cli("WA2", restore=True)
        self.assertEqual(str(e.exception), "pilot: WA2 (Flight b) is not archived")

    def test_a_flight_the_order_never_placed_comes_back_last(self):
        pilot.PRIORITY.unlink()
        self.cli("WA1")                                  # `a`, the most recently touched
        self.cli("WA1", restore=True)                    # and touched again by its log line
        self.assertEqual(self.order(), ["b", "c", "a"])

    def test_legs_still_flying_refuse_it_unless_forced(self):
        self.flying()
        with self.assertRaises(SystemExit) as e:
            self.cli("WA2")
        self.assertEqual(str(e.exception), "pilot: WA2 (Flight b) has legs still flying: fix. Archiving it gives up on them; "
                                           "`python pilot.py archive --force WA2` archives it anyway.")
        self.assertEqual(self.order(), ["c", "b", "a"])
        self.cli("WA2", force=True)
        self.assertEqual(self.order(), ["c", "a"])
        self.assertEqual(self.log_of("b")[-1][17:], "Archived by the controller, with fix still flying")
        self.assertEqual(pilot.flight_history([])[0]["flyingAtArchive"], ["fix"])

    def test_a_pilot_never_archives_a_flight(self):
        with unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": "a"}):
            with self.assertRaises(SystemExit) as e:
                self.cli("WA2")
        self.assertIn("only the controller archives a flight", str(e.exception))
        self.assertEqual(pilot.archived_flights(), {})

    def test_an_unknown_flight(self):
        with self.assertRaises(SystemExit) as e:
            self.cli("ZZ9")
        self.assertEqual(str(e.exception), "pilot: no flight plan has the number ZZ9")

    def test_a_connection_to_an_archived_flight_still_resolves_by_its_own_state(self):
        self.plan("s2", CONNECTED)
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA9\n\nConnects: TIA2"))
        pilot.archive_flight("TIA2")
        git = {"ahead": 0, "prs": [], "remotes": []}
        with unittest.mock.patch.object(pilot, "collateral", lambda w, ttl=20: git), unittest.mock.patch.object(pilot, "collateral_now", lambda w: git):
            c = pilot.connection("TIA2", [], [])
            self.assertEqual((c["known"], c["landed"], c["sessionId"]), (True, False, "s2"))
            pilot.write_json(pilot.ARCHIVE / "build-1.json", {"name": "build", "plan": "s2", "archivedAt": 1, "repo": "web-app", "branch": "worktree-w-build",
                             "final": {"reportHead": "DONE", "checklist": [{"status": "completed", "subject": "x"}]}})
            p = next(p for p in pilot.plans_view([], []) if p["sessionId"] == "s1")
        self.assertEqual([(c["flight"], c["known"], c["landed"]) for c in p["connections"]], [("TIA2", True, True)])

    def test_a_woken_pilots_archived_flight_stays_archived(self):
        pilot.archive_flight("WA2")
        pilot.move_plan("b", "b2")
        self.assertEqual(list(pilot.archived_flights()), ["b2"])
        self.assertNotIn("b2", self.order())

    def test_the_endpoint_names_legs_flying_and_archives_when_forced(self):
        import threading
        import urllib.error
        import urllib.request
        self.flying()
        srv = pilot.http.server.ThreadingHTTPServer(("127.0.0.1", 0), pilot.Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            def post(path, body, headers={"X-Pilot": "1"}):
                req = urllib.request.Request(f"http://127.0.0.1:{srv.server_address[1]}{path}", json.dumps(body).encode(),
                                             {"Content-Type": "application/json", **headers}, method="POST")
                try:
                    with urllib.request.urlopen(req) as r:
                        return r.status, json.loads(r.read())
                except urllib.error.HTTPError as e:
                    with e:
                        return e.code, json.loads(e.read())
            self.assertEqual(post("/api/flights/b/archive", {}, headers={})[0], 403, "only the dashboard's page archives")
            code, body = post("/api/flights/b/archive", {})
            self.assertEqual((code, body["flying"]), (409, ["fix"]))
            self.assertEqual(post("/api/flights/b/archive", {"force": True}), (200, {"ok": True, "sessionId": "b"}))
            self.assertEqual(self.order(), ["c", "a"])
            self.assertEqual(post("/api/flights/WA2/restore", {}), (200, {"ok": True, "sessionId": "b"}))
            self.assertEqual(self.order(), ["c", "b", "a"])
            self.assertEqual(post("/api/flights/nope/restore", {})[0], 409)
        finally:
            srv.shutdown()
            srv.server_close()


class HoldTests(Case):
    """A flight launched to plan only holds at the gate on its plan's `Hold:` line: `start` refuses its legs until
    the controller clears it, which lifts the line, logs it and tells or wakes the pilot."""

    def setUp(self):
        super().setUp()
        self.told, self.woken = [], []
        self._fns = pilot.intercom, pilot.wake_pilot
        pilot.intercom = lambda names, text, sender="Controller": self.told.append((names, text)) or "delivered"
        pilot.wake_pilot = lambda old, why: self.woken.append((old, why)) or "new-sid-1234"

    def tearDown(self):
        pilot.intercom, pilot.wake_pilot = self._fns
        super().tearDown()

    def start(self, sid="s1"):
        with unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": sid}):
            with self.assertRaises(SystemExit) as e:
                pilot.cmd_start(types.SimpleNamespace(name="leg", after=None))
        return str(e.exception)

    def clear(self, flight="WA1"):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            pilot.cmd_clear(types.SimpleNamespace(flight=flight))
        return out.getvalue()

    def open_plan(self, sid="s1", hold=False, top="", cwd="C:/elsewhere"):
        """`plan` as the pilot session `sid` runs it: no --session, its id from the environment."""
        with unittest.mock.patch.object(pilot.subprocess, "run", lambda *a, **kw: types.SimpleNamespace(stdout=top)), \
                unittest.mock.patch.object(pilot.os, "getcwd", lambda: cwd), \
                unittest.mock.patch.object(pilot, "register_repo", lambda path: None), \
                unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": sid}), \
                contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()):
            pilot.cmd_plan(types.SimpleNamespace(session=None, title="Held", hold=hold))
        return Path(out.getvalue().strip())

    def test_the_hold_line_parses_and_is_neither_notes_nor_waiting(self):
        text = PLAN.replace("Recommend: 16M.\n", "Recommend: 16M.\nHold: awaiting clearance\n")
        p = pilot.parse_plan(text)
        self.assertEqual(p["hold"], "awaiting clearance")
        self.assertNotIn("Hold", p["waiting"])
        self.assertEqual(p["waiting"].count("\n- "), 1, "the waiting list ends at the header line")
        self.assertEqual(p["notes"], "The exporter rounds rotation twice; that is the bug.")
        self.assertIsNone(pilot.parse_plan(PLAN)["hold"])

    def test_plan_hold_files_the_line_once_under_the_flight_and_logs_it(self):
        self.plan("s1", PLAN)
        self.open_plan(hold=True)
        self.open_plan(hold=True)
        text = pilot.plan_path("s1").read_text(encoding="utf-8")
        self.assertEqual(text.count("Hold:"), 1)
        self.assertIn("Flight: WA1\n\nHold: awaiting clearance\n", text)
        self.assertTrue(self.log_of("s1")[-1].endswith("Holding at the gate: awaiting clearance for takeoff from the controller"))
        p = self.open_plan("s9", hold=True)
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance", "a new plan opens holding")

    def test_plan_hold_is_refused_once_a_leg_has_taken_off(self):
        self.plan("s1", PLAN)
        self.worker("design", plan="s1")
        with self.assertRaises(SystemExit) as e:
            self.open_plan(hold=True)
        self.assertIn("WA1 has already taken off (design)", str(e.exception))
        self.assertNotIn("Hold:", pilot.plan_path("s1").read_text(encoding="utf-8"))

    def test_start_is_refused_while_held_and_goes_on_once_cleared(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        msg = self.start()
        self.assertEqual(msg, pilot.HOLD_REFUSAL.format(flight="WA1"))
        self.assertIn("WA1 is holding at the gate, awaiting clearance for takeoff from the controller", msg)
        self.assertIn("end your turn without starting a leg", msg)
        self.assertFalse(pilot.WORKERS.exists() and list(pilot.WORKERS.glob("*.json")), "nothing took off")
        self.clear()
        pilot.write_json(pilot.DAEMON_LOCK, {"pid": os.getpid(), "procStartFt": "1"})
        self.assertIn("daemon lock", self.start(), "past the hold, to the next check")

    def test_clear_lifts_the_hold_logs_it_and_tells_a_live_pilot(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        self.rows = [{"name": "pilot-web", "sessionId": "s1", "kind": "interactive", "pid": 42}]
        out = self.clear("wa1")
        self.assertEqual(out.strip(), "WA1 (Export fixes) cleared for takeoff: told its pilot pilot-web through the intercom")
        text = pilot.plan_path("s1").read_text(encoding="utf-8")
        self.assertNotIn("Hold:", text)
        self.assertIn("Flight: WA1\n\nObjective:", text)
        self.assertTrue(self.log_of("s1")[-1].endswith("Cleared for takeoff by the controller"))
        bb = [json.loads(l) for l in pilot.BLACKBOX.read_text(encoding="utf-8").splitlines()]
        self.assertEqual((bb[-1]["text"], bb[-1]["actor"], bb[-1]["flight"]), ("Cleared for takeoff by the controller", "controller", "WA1"))
        self.assertEqual(self.told, [(["pilot-web"], pilot.CLEARED.format(flight="WA1"))])
        self.assertIn("Take off", self.told[0][1])
        self.assertEqual(self.woken, [])
        with self.assertRaises(SystemExit) as e:
            self.clear()
        self.assertEqual(str(e.exception), "pilot: WA1 (Export fixes) is not holding for clearance")

    def test_clear_wakes_a_pilot_whose_session_has_ended(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        self.rows = [{"name": "pilot-web", "sessionId": "s1", "kind": "background", "pid": None}]
        self.assertIn("cleared for takeoff: woke its pilot as new-sid-", self.clear())
        self.assertEqual(self.woken, [("s1", pilot.CLEARED.format(flight="WA1"))])
        self.assertEqual(self.told, [])
        self.rows = []
        self.plan("s2", CONNECTED.replace("Flight: TIA2", "Flight: TIA2\n\nHold: awaiting clearance"))
        self.assertIn("its pilot is not running", self.clear("TIA2"))

    def test_a_clearance_that_cannot_reach_its_pilot_still_lands_and_says_so(self):
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        self.rows = [{"name": "pilot-web", "sessionId": "s1", "kind": "background", "pid": None}]

        def fail(old, why):
            raise SystemExit("pilot: claude --bg failed")
        pilot.wake_pilot = fail
        out = self.clear()           # exits normally: the hold is lifted whatever the wake did
        self.assertEqual(out.strip(), "WA1 (Export fixes) cleared for takeoff: hold lifted, but its pilot could not be reached "
                                      "(pilot: claude --bg failed); tell it to take off")
        self.assertNotIn("Hold:", pilot.plan_path("s1").read_text(encoding="utf-8"))
        self.plan("s2", CONNECTED.replace("Flight: TIA2", "Flight: TIA2\n\nHold: awaiting clearance"))
        self.rows = [{"name": "pilot-tia", "sessionId": "s2", "kind": "interactive", "pid": 7}]
        pilot.intercom = lambda names, text, sender="Controller": (_ for _ in ()).throw(ValueError("the intercom failed (1)"))
        self.assertIn("could not be reached (the intercom failed (1))", self.clear("TIA2"))
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        self.rows = [{"name": "pilot-web", "sessionId": "s1", "kind": "background", "pid": None}]

        def no_claude(old, why):
            raise FileNotFoundError("[WinError 2] The system cannot find the file specified")
        pilot.wake_pilot = no_claude                           # claude not on PATH
        self.assertIn("could not be reached ([WinError 2] The system cannot find the file specified)", self.clear())
        self.assertNotIn("Hold:", pilot.plan_path("s1").read_text(encoding="utf-8"))

    def test_clear_refuses_an_unknown_flight_and_a_pilot(self):
        with self.assertRaises(SystemExit) as e:
            self.clear("ZZ9")
        self.assertEqual(str(e.exception), "pilot: no flight plan has the number ZZ9")
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        with unittest.mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": "s1"}):
            with self.assertRaises(SystemExit) as e:
                self.clear()
        self.assertIn("only the controller clears a flight for takeoff", str(e.exception))
        self.assertIn("Hold:", pilot.plan_path("s1").read_text(encoding="utf-8"))

    def launch(self, where="terminal", resume=None, hold=True, as_pilot=True, sid="bg-sid", path="C:/repos/web-app"):
        projects = [{"path": path, "trusted": True}]
        sent = []
        with unittest.mock.patch.object(pilot, "known_projects", lambda: projects), \
                unittest.mock.patch.object(pilot, "register_repo", lambda path: None), \
                unittest.mock.patch.object(pilot, "project_dir", lambda path: ROOT / "proj"), \
                unittest.mock.patch.object(pilot, "claude", lambda *a, **kw: sent.append((a, kw.get("stdin")))), \
                unittest.mock.patch.object(pilot, "backgrounded", lambda r: "job1"), \
                unittest.mock.patch.object(pilot, "session_of", lambda job: sid), \
                unittest.mock.patch.object(pilot.shutil, "which", lambda name: "wt.exe"), \
                unittest.mock.patch.object(pilot.subprocess, "Popen", lambda *a, **kw: sent.append((a, None))):
            out = pilot.launch(path, resume, where, "Export fixes", as_pilot, hold)
        return out, sent

    def test_the_launch_option_opens_the_new_pilots_plan_held_and_tells_it(self):
        out, sent = self.launch("background")
        self.assertEqual((out["where"], out["hold"]), ("background", True))
        self.assertTrue(sent[0][1].startswith("/pilot:pilot Export fixes\n\nPlan only:"), sent[0][1])
        self.assertIn("plan --hold", sent[0][1])
        self.assertTrue(self.open_plan("other-sid", top="C:/repos/web-app").exists())
        self.assertIsNone(pilot.parse_plan(pilot.plan_path("other-sid").read_text(encoding="utf-8"))["hold"],
                          "another session in that directory is not the one launched")
        p = self.open_plan("bg-sid", top="C:/repos/web-app")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.assertIn("launched to plan only", self.log_of("bg-sid")[-1])
        self.assertEqual(pilot.live_holds(), [], "the hold is used up")

    def test_a_terminal_launch_holds_the_next_plan_opened_in_its_directory(self):
        out, sent = self.launch("terminal")
        self.assertTrue(out["hold"])
        msg = next(f for f in (pilot.STATE / "launch").glob("*.txt")).read_text(encoding="utf-8")
        self.assertIn("Plan only: this flight holds at the gate", msg)
        p = self.open_plan("tab-sid", top="C:\\repos\\web-app")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.assertIsNone(pilot.parse_plan(self.open_plan("next-sid", top="C:/repos/web-app").read_text(encoding="utf-8"))["hold"])

    def test_a_hold_booked_by_directory_matches_a_subfolder_project_or_one_in_no_repo(self):
        self.launch("terminal", path="C:/repos/web-app/src")      # a project in a subfolder of a repo
        p = self.open_plan("sub-sid", top="C:/repos/web-app", cwd="C:/repos/web-app/src")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.launch("terminal", path="C:/notes")                  # a folder in no git repo
        p = self.open_plan("nogit-sid", top="", cwd=r"C:\notes")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.assertEqual(pilot.live_holds(), [])

    def test_plan_with_session_takes_no_hold_booked_by_directory(self):
        self.launch("terminal")                                   # booked by directory, for the launched pilot
        with contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()), \
                unittest.mock.patch.object(pilot.subprocess, "run", lambda *a, **kw: types.SimpleNamespace(stdout="C:/repos/web-app")), \
                unittest.mock.patch.object(pilot.os, "getcwd", lambda: "C:/repos/web-app"):
            pilot.cmd_plan(types.SimpleNamespace(session="ctl-sid", title="By hand", hold=False))
        self.assertIsNone(pilot.parse_plan(Path(out.getvalue().strip()).read_text(encoding="utf-8"))["hold"])
        self.assertEqual(len(pilot.live_holds()), 1, "left for the launched pilot")
        p = self.open_plan("tab-sid", top="", cwd="C:/repos/web-app")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")

    def test_a_launch_resuming_a_flight_that_has_taken_off_is_refused(self):
        sid = "2" * 8 + "-0000-0000-0000-" + "2" * 12
        (ROOT / "proj").mkdir(exist_ok=True)
        (ROOT / "proj" / f"{sid}.jsonl").write_text("", encoding="utf-8")
        self.plan(sid, PLAN)
        self.worker("design", plan=sid)                           # a leg in the air
        with self.assertRaises(ValueError) as e:
            self.launch("terminal", resume=sid)
        self.assertEqual(str(e.exception), "pilot: WA1 has already taken off (design), so it cannot hold at the gate; "
                                           "a flight holds for clearance only before its first leg takes off")
        self.assertNotIn("Hold:", pilot.plan_path(sid).read_text(encoding="utf-8"))
        self.assertEqual(pilot.live_holds(), [])

    def test_a_flight_whose_legs_landed_and_were_removed_cannot_hold_either(self):
        self.plan("s1", PLAN)
        pilot.write_json(pilot.ARCHIVE / "design-1.json", {"name": "design", "plan": "s1", "archivedAt": 1})
        with self.assertRaises(SystemExit) as e:
            self.open_plan(hold=True)
        self.assertIn("WA1 has already taken off (design)", str(e.exception))
        pilot.ARCHIVE.joinpath("design-1.json").unlink()
        pilot.write_json(pilot.SCHEDULES, {"s1": {"eta": 1, "set": 1}})   # its departure, kept by its schedule
        with self.assertRaises(SystemExit) as e:
            self.open_plan(hold=True)
        self.assertIn("WA1 has already taken off (its schedule was set when it departed)", str(e.exception))
        self.assertNotIn("Hold:", pilot.plan_path("s1").read_text(encoding="utf-8"))

    def test_a_launch_resuming_a_pilot_with_a_plan_holds_it_at_once(self):
        sid = "0" * 8 + "-0000-0000-0000-" + "0" * 12
        (ROOT / "proj").mkdir(exist_ok=True)
        (ROOT / "proj" / f"{sid}.jsonl").write_text("", encoding="utf-8")
        self.plan(sid, PLAN)
        self.launch("terminal", resume=sid)
        self.assertEqual(pilot.parse_plan(pilot.plan_path(sid).read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.assertEqual(pilot.live_holds(), [])

    def test_a_background_resume_holds_the_copys_plan_not_the_old_one(self):
        sid = "1" * 8 + "-0000-0000-0000-" + "1" * 12
        (ROOT / "proj").mkdir(exist_ok=True)
        (ROOT / "proj" / f"{sid}.jsonl").write_text("", encoding="utf-8")
        self.plan(sid, PLAN)
        self.launch("background", resume=sid, sid="copy-sid")      # --bg --resume continues as a copy
        self.assertNotIn("Hold:", pilot.plan_path(sid).read_text(encoding="utf-8"))
        p = self.open_plan("copy-sid", top="C:/repos/web-app")
        self.assertEqual(pilot.parse_plan(p.read_text(encoding="utf-8"))["hold"], "awaiting clearance")
        self.assertEqual(pilot.live_holds(), [])

    def test_without_the_option_nothing_holds_and_plan_only_needs_a_pilot(self):
        out, sent = self.launch("background", hold=False)
        self.assertNotIn("hold", out)
        self.assertNotIn("Plan only", sent[0][1])
        self.assertEqual(pilot.live_holds(), [])
        with self.assertRaises(ValueError):
            self.launch("background", as_pilot=False)

    def test_the_dashboard_says_a_held_flight_has_no_departure_and_the_endpoint_clears_it(self):
        import threading
        import urllib.error
        import urllib.request
        self.plan("s1", PLAN.replace("Flight: WA1", "Flight: WA1\n\nHold: awaiting clearance"))
        p = pilot.plans_view([], [])[0]
        self.assertEqual((p["hold"], p["times"]["departed"], p["times"]["noEta"]), ("awaiting clearance", None, "awaiting clearance: no departure yet"))
        self.assertIn("Waiting on you: 2 items", pilot.track_html("s1"), "what waits on the controller comes first")
        self.plan("s1", re.sub(r"## Waiting on the controller\n\n(- .*\n)+", "", pilot.plan_path("s1").read_text(encoding="utf-8")))
        self.assertIn("Holding at the gate: ready, awaiting clearance", pilot.track_html("s1"))
        srv = pilot.http.server.ThreadingHTTPServer(("127.0.0.1", 0), pilot.Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            def post(path):
                req = urllib.request.Request(f"http://127.0.0.1:{srv.server_address[1]}{path}", b"{}",
                                             {"Content-Type": "application/json", "X-Pilot": "1"}, method="POST")
                try:
                    with urllib.request.urlopen(req) as r:
                        return r.status, json.loads(r.read())
                except urllib.error.HTTPError as e:
                    with e:
                        return e.code, json.loads(e.read())
            self.assertEqual(post("/api/flights/WA1/clear"), (200, {"ok": True, "sessionId": "s1", "reached": "its pilot is not running; tell it to take off when it is back"}))
            self.assertIsNone(pilot.parse_plan(pilot.plan_path("s1").read_text(encoding="utf-8"))["hold"])
            self.assertEqual(post("/api/flights/WA1/clear")[0], 409)
            self.assertEqual(post("/api/flights/ZZ9/clear")[0], 409)
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
            self.assertIn("to the controller", self.log_of("p1")[-1])
            text, actor = self.last_box()           # reworded from "to the tower": still pilot.py's line
            self.assertTrue(text.startswith("Review round 2 of leg-a") and text.endswith("to the controller; 1 question(s). Bugs: Saving drops the last row."), text)
            self.assertEqual(actor, "pilot.py")
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

    def test_a_preview_reads_the_real_schedules_and_writes_none(self):
        self.plan("p4", PLAN)
        self.assertEqual(pilot.schedule("p4", 50_000.0), 50_000.0)
        disk = pilot.SCHEDULES.read_bytes()
        self.assertEqual(pilot.schedule("p4", 90_000.0, persist=False), 50_000.0, "its delay is the dashboard's")
        self.assertEqual(pilot.schedule("p5", 60_000.0, persist=False), 60_000.0)
        self.assertEqual(pilot.SCHEDULES.read_bytes(), disk)

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
        cases = [("pilot budget: this worker has spent 10,000,000 of its 10,000,000 weighted tokens.", "pilot.py's budget", False),
                 ("pilot: don't ask in the terminal. A prompt there blocks you", "pilot's ask hook", True),
                 ("PreToolUse:Bash hook error: pilot budget: this worker has spent 10,000,000", "pilot.py's budget", False),
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
        self.assertEqual(self.last_box()[1], "watchtower")

    def test_gates_hold_without_asking_the_watchtower(self):
        approved, why = self.refuel(25_000_000)
        self.assertFalse(approved)
        self.assertIn("past 2 times", why)
        pilot.set_refuel_mode("manual")
        self.assertEqual(self.last_box(), ("Refuelling set to manual: every refuel goes to the controller", "controller"))
        approved, why = self.refuel(12_000_000)
        self.assertFalse(approved)
        self.assertIn("manual", why)
        self.assertEqual(self.asked, [])
        self.assertEqual(pilot.load("fix")["budget"], 10_000_000)
        self.assertIn("Watchtower sent to the controller", self.log_of("s1")[-1])
        self.assertEqual(self.last_box()[1], "watchtower")      # reworded from "sent to the tower"

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


class RegisterRepoTests(Case):
    def test_a_launched_repo_is_added_once_under_its_folder_name(self):
        repos = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, repos, True)
        for d in ("a/graphjoy", "b/graphjoy", "c/plain"):
            (repos / d).mkdir(parents=True)
        (repos / "a/graphjoy/.git").mkdir()
        (repos / "b/graphjoy/.git").write_text("gitdir: elsewhere")      # a worktree's .git is a file
        self.config(repos={"pilot": "C:/x/pilot"}, budget=5)
        self.assertEqual(pilot.register_repo(str(repos / "a/graphjoy")), "graphjoy")
        self.assertIsNone(pilot.register_repo(str(repos / "a" / "graphjoy")), "already named")
        self.assertEqual(pilot.register_repo(str(repos / "b/graphjoy")), "graphjoy-2")
        self.assertIsNone(pilot.register_repo(str(repos / "c/plain")), "not a git checkout")
        cfg = pilot.config()
        self.assertEqual(set(cfg["repos"]), {"pilot", "graphjoy", "graphjoy-2"})
        self.assertEqual(cfg["budget"], 5, "the rest of the config is kept")


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
                 ({"state": "done", "reportHead": "DONE", "checklist": two[:1]}, ("good", "done")),
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
        self.assertIn("done, not landed · removed", page, "its pull requests not read: done, not landed")
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

    def setUp(self):
        super().setUp()
        # Each leg's git, as ConnectionTests has it: read, no commits past its base, no PR.
        self.git = {"ahead": 0, "prs": [], "remotes": []}
        self._git_fns = pilot.collateral_now, pilot.collateral
        pilot.collateral_now, pilot.collateral = (lambda w: self.git), (lambda w, ttl=20: self.git)

    def tearDown(self):
        pilot.collateral_now, pilot.collateral = self._git_fns
        super().tearDown()

    def archive(self, leg, head="DONE", **kw):
        pilot.write_json(pilot.ARCHIVE / f"{leg}-1.json", {"name": leg, "plan": "s1", "archivedAt": 1, "started": 1000.0,
                         "repo": "web-app", "branch": f"worktree-w-{leg}", **kw,
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
        self.assertEqual(times["basis"], "all 4 legs done")
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
        self.assertIn("Leg api-b took off: worker", self.log_of("s1")[-1])
        self.assertTrue({s["leg"]: s for s in self.states()}["api-b"]["taken"])

    def test_start_refuses_a_primary_while_its_alternate_has_a_worker(self):
        self.plan("s1", LANES)
        self.worker("api-b", plan="s1", divertedFrom="api", divertReason="x")
        self.assertIn("api was diverted to api-b; `pilot.py rm api-b` first to undo the diversion", self.start("api"))
        (pilot.WORKERS / "api-b.json").unlink()                     # rm undoes it
        self.assertIn("daemon lock", self.start("api"))


class LandedLookupTests(Case):
    """A leg whose PR was squash- or rebase-merged and its branch deleted on merge (as tianwen does), then pruned
    by a fetch: no remote branch holds its tip, so its PRs are found by the tip itself."""

    def setUp(self):
        super().setUp()
        self.repo = Path(tempfile.mkdtemp(prefix="lookup-repo-", dir=ROOT))   # one each: git's read-only objects outlive rmtree on Windows
        self.git("init", "-q", "-b", "main")
        self.git("commit", "-q", "--allow-empty", "-m", "base")
        self.git("checkout", "-q", "-b", "worktree-w-build")
        self.git("commit", "-q", "--allow-empty", "-m", "the fix")
        self.tip = self.git("rev-parse", "HEAD")
        self.git("checkout", "-q", "main")
        self.git("remote", "add", "origin", "https://github.com/example/web-app.git")   # never fetched: no remote refs
        self.config(repos={"web-app": str(self.repo)})
        self.w = {"name": "build", "repo": "web-app", "branch": "worktree-w-build", "base": "main",
                  "worktree": str(self.repo / "gone"), "started": 0}
        self.asked = []
        self.answers = {}

    def git(self, *a):
        return subprocess.run(["git", "-C", str(self.repo), "-c", "user.name=t", "-c", "user.email=t@t", *a],
                              check=True, capture_output=True, text=True).stdout.strip()

    def gh(self, root, *a):
        """A fake gh: each kind of call (api, view, list) answers from self.answers as (code, stdout, stderr)."""
        self.asked.append(a)
        kind = "api" if a[0] == "api" else a[1]
        code, out, err = self.answers.get(kind, (0, "[]", ""))
        if kind == "view":
            out = json.dumps(out[int(a[2])]) if isinstance(out, dict) else out
        return types.SimpleNamespace(returncode=code, stdout=out, stderr=err)

    def pr(self, number, state="MERGED", head=None, branch="feat/fix"):
        return {"number": number, "state": state, "headRefName": branch, "headRefOid": head or self.tip, "url": f"u/{number}"}

    def read(self):
        with unittest.mock.patch.object(pilot, "gh_run", self.gh), unittest.mock.patch.object(pilot.shutil, "which", lambda n: "gh"):
            return pilot.collateral_now(self.w)

    def landed(self, g):
        return pilot.flight_landed([{"leg": "build", "w": {"reportHead": "DONE"}, "cls": "good", "label": "landed", "git": g}])

    def test_a_squash_merged_leg_with_no_tracking_ref_lands(self):
        self.answers = {"api": (0, json.dumps([{"number": 12, "head": {"sha": self.tip}}]), ""),
                        "view": (0, {12: self.pr(12)}, "")}
        g = self.read()
        self.assertEqual((g["remotes"], g["ahead"], g["tip"]), ([], 1, self.tip))
        self.assertEqual([(p["number"], p["state"], p["head"]) for p in g["prs"]], [(12, "MERGED", self.tip)])
        self.assertEqual(self.asked[0], ("api", f"repos/{{owner}}/{{repo}}/commits/{self.tip}/pulls"))
        self.assertEqual(len(self.asked), 2, "the tip is the PR's head: no search after")
        self.assertEqual(self.landed(g), (True, []))

    def test_the_search_finds_a_merged_pr_the_commit_endpoint_does_not(self):
        self.answers = {"api": (0, "[]", ""), "list": (0, json.dumps([self.pr(14)]), "")}
        g = self.read()
        self.assertEqual(self.asked[1][:6], ("pr", "list", "--state", "merged", "--search", self.tip))
        self.assertEqual([p["number"] for p in g["prs"]], [14])
        self.assertEqual(self.landed(g), (True, []))

    def test_an_unmerged_tip_with_no_ref_stays_open(self):
        self.answers = {"api": (1, "", f"gh: No commit found for SHA: {self.tip} (HTTP 422)")}
        g = self.read()
        self.assertEqual(g["prs"], [], "a tip GitHub never saw has no PR: known, not unknown")
        self.assertEqual(self.landed(g), (False, ["build: 1 commit on no merged pull request"]))

    def test_gh_failing_on_the_sha_lookup_stays_unknown(self):
        for answers in ({"api": (1, "", "gh: HTTP 502")}, {"api": (0, "not json", "")},
                        {"api": (0, json.dumps([{"number": 12}]), ""), "view": (1, "", "gh: HTTP 502")},
                        {"api": (0, "[]", ""), "list": (1, "", "gh: rate limited")}):
            self.answers, self.asked = answers, []
            g = self.read()
            self.assertIsNone(g["prs"], answers)
            self.assertEqual(self.landed(g), (False, ["build: its pull requests not read yet"]), answers)
        with unittest.mock.patch.object(pilot.shutil, "which", lambda n: None):
            self.assertIsNone(pilot.sha_prs(self.repo, self.tip))

    def test_a_merged_pr_whose_head_is_not_the_tip_does_not_count(self):
        old = self.pr(5, head="0" * 40, branch="worktree-w-build")       # a branch name used again, or commits pushed after
        self.answers = {"api": (0, json.dumps([{"number": 5}]), ""), "view": (0, {5: old}, ""),
                        "list": (0, json.dumps([old]), "")}
        g = self.read()
        self.assertEqual([p["number"] for p in g["prs"]], [5], "the search's answer is not counted twice")
        self.assertEqual(self.landed(g), (False, ["build: 1 commit on no merged pull request (#5 merged at another head)"]))
        g["prs"].append({"number": 6, "state": "MERGED", "head": self.tip})
        self.assertEqual(self.landed(g), (True, []))

    def test_a_repo_with_no_remote_asks_github_nothing(self):
        self.git("remote", "remove", "origin")
        g = self.read()
        self.assertEqual((g["prs"], self.asked), ([], []))

    def test_rm_reads_a_gone_worktrees_commits_from_its_branch_and_none_once_that_is_gone(self):
        def rm():
            self.worker("build", plan="s1", base="main", branch="worktree-w-build", worktree=str(self.repo / "gone"),
                        sessions=[{"id": "job-build", "sessionId": "sess-build"}])
            fake = lambda *a, timeout=60, **_: types.SimpleNamespace(returncode=0, stdout="", stderr="")
            with unittest.mock.patch.object(pilot, "claude", fake), contextlib.redirect_stdout(io.StringIO()):
                pilot.cmd_rm(types.SimpleNamespace(name="build"))
            rec = max(pilot.ARCHIVE.glob("build-*.json"), key=lambda p: p.stat().st_mtime_ns)
            commits = json.loads(rec.read_text(encoding="utf-8"))["final"]["commits"]
            rec.unlink()
            return commits
        self.assertEqual(rm(), self.git("log", "--oneline", "main..worktree-w-build"))
        self.assertIn("the fix", rm())
        self.git("branch", "-q", "-D", "worktree-w-build")
        self.assertIsNone(rm(), "unreadable, not \"\": that would say it had no commits")
        st = [{"leg": "build", "w": {"final": {"reportHead": "DONE", "commits": None}}, "cls": "good", "label": "landed",
               "git": {"gone": True, "repo": "web-app", "branch": "worktree-w-build"}}]
        self.assertEqual(pilot.flight_landed(st), (False, ["build: its branch is deleted, so its commits cannot be read"]))


class FlightHistoryLegTests(Case):
    """History reads an archived flight's legs as the strips do: flight_legs' states and flown()'s route."""

    def setUp(self):
        super().setUp()
        self.git = {"ahead": 1, "prs": [], "remotes": []}
        self._git_fns = pilot.collateral_now, pilot.collateral
        pilot.collateral_now, pilot.collateral = (lambda w: self.git), (lambda w, ttl=20: self.git)

    def tearDown(self):
        pilot.collateral_now, pilot.collateral = self._git_fns
        super().tearDown()

    def removed(self, leg, plan="s1"):
        pilot.write_json(pilot.ARCHIVE / f"{leg}-1.json", {"name": leg, "plan": plan, "archivedAt": 1, "started": 1000.0,
                         "repo": "web-app", "branch": f"worktree-w-{leg}",
                         "final": {"reportHead": "DONE", "checklist": [{"status": "completed", "subject": "x"}]}})

    def legs(self):
        return [(l["leg"], l["cls"], l["label"]) for l in pilot.flight_history([], pilot.archived(200))[0]["legs"]]

    def test_a_removed_leg_whose_pr_is_in_review_has_not_landed(self):
        self.plan("s1", PLAN)
        self.removed("fix")
        self.git["prs"] = [{"number": 7, "state": "OPEN", "head": "abc", "ci": "passing", "reviewer": "self"}]
        pilot.archive_flight("WA1")
        self.assertEqual(self.legs(), [("design", "planned", "planned"), ("fix", "working", "on approach")])
        self.git["prs"][0]["state"] = "MERGED"
        self.assertEqual(self.legs()[1], ("fix", "good", "landed"))

    def test_an_alternate_not_taken_is_left_out(self):
        self.plan("s1", LANES)
        self.removed("scaffold")
        pilot.archive_flight("WA2")
        self.assertEqual([l[0] for l in self.legs()], ["scaffold", "api", "ui", "ship"])
        self.removed("api-b")                                   # a removed alternate that landed was taken
        self.assertEqual([l[0] for l in self.legs()], ["scaffold", "api", "ui", "api-b", "ship"])


WAITING_PLAN = """# Flight plan: Notices

Flight: NT1

## Route

| Leg | Goal | After | Done when |
|---|---|---|---|
| a | x | - | y |

## Waiting on the controller

{items}

## Log
"""


class NoticeTests(Case):
    """The bell acks a notice on its own once later ones supersede it: folded, settled by rule, judged by Haiku
    (stubbed; the real one is never called), and kept by the guard whatever Haiku says."""

    def setUp(self):
        super().setUp()
        self.n = pilot.Notices()
        self.n.spawn = lambda fn: fn()            # the judge's thread, run in line
        self.snap = {"workers": [], "pilots": [], "plans": [], "autopilot": {}}
        self.prs = {}
        self.config(repos={"web-app": str(ROOT)})
        self._mentioned, pilot.mentioned_prs = pilot.mentioned_prs, lambda text, ttl=60: [
            {"repo": r, "number": k, "state": self.prs[(r, k)]} for r, k in pilot.pr_refs(text) if (r, k) in self.prs]

    def tearDown(self):
        pilot.mentioned_prs = self._mentioned
        super().tearDown()

    def note(self, id, kind, text, subject, at=None, head=None, read=False, items=None, worker="a"):
        n = {"id": id, "at": at or float(len(self.n.items) + 1), "kind": kind, "worker": worker, "sessionId": "s-" + worker,
             "text": text, "read": read, "href": None, "subject": subject, "head": head, **({"items": items} if items else {})}
        self.n.items.append(n)
        return n

    def by(self, id):
        return next(i for i in self.n.items if i["id"] == id)

    def settle(self):
        return self.n.settle(pilot.notice_context(self.snap, self.n.items))

    def waiting(self, sid, *items):
        self.plan(sid, WAITING_PLAN.format(items="\n".join(f"- {i}" for i in items)))

    def leg(self, name, state):
        self.snap["workers"] = [w for w in self.snap["workers"] if w["name"] != name] + [
            {"name": name, "state": state, "sessionId": "s-" + name, "lastActivity": None, "overBudget": False, "budget": 1}]

    def update(self, at):
        with unittest.mock.patch.object(pilot.time, "time", lambda: at):
            return self.n.update(self.snap)

    # ---- folding

    def test_an_identical_notice_for_the_same_session_within_a_minute_is_one(self):
        p = {"sessionId": "P", "name": "pp", "status": "busy", "flying": [], "said": None}
        self.snap["pilots"] = [p]
        self.update(1000)                                   # the first look records it, no notice
        for at, status in [(1001, "idle"), (1004, "busy"), (1006, "idle"), (1009, "busy"), (1012, "idle")]:
            p["status"] = status
            self.update(at)
        self.assertEqual(len(self.n.items), 1)
        self.assertEqual((self.n.items[0]["times"], self.n.items[0]["last"]), (3, 1012))
        self.assertEqual(json.loads(pilot.NOTICES.read_text(encoding="utf-8"))["items"][0]["times"], 3)   # on disk, not just shown
        p["status"] = "busy"
        self.update(1050)
        p["status"] = "idle"
        self.update(1080)                                   # 68 s after the last fold: a notice of its own
        self.assertEqual(len(self.n.items), 2)
        self.assertEqual(self.n.items[0]["settled"], f"superseded by {self.n.items[1]['id']}")   # a pilot's later turn ended

    def test_a_question_asked_again_within_a_minute_after_it_was_answered_is_a_new_unread_notice(self):
        p = {"sessionId": "P", "name": "pp", "status": "busy", "flying": [], "said": None, "waitingFor": "input"}
        self.snap["pilots"] = [p]
        self.update(1000)
        p["status"] = "waiting"
        self.update(1001)
        p["status"] = "busy"                                # answered: settled and read
        self.update(1010)
        self.assertEqual(self.n.items[0]["settled"], "answered")
        p["status"] = "waiting"                             # asked again, within the minute
        self.update(1020)
        self.assertEqual([i["read"] for i in self.n.items], [True, False])
        self.assertEqual(self.n.view()["unread"], 1)

    def plan_waits(self, at, *items):
        self.waiting("s1", *items)
        self.snap["plans"] = [{"sessionId": "s1", "title": "Notices", "pilot": None,
                               "waiting": pilot.split_waiting(pilot.plan_path("s1").read_text(encoding="utf-8"))[0]}]
        self.update(at)

    def unread_items(self):
        return [i.get("items") for i in self.n.items if not i["read"]]

    def test_a_waiting_item_replaced_within_a_minute_is_a_new_unread_notice(self):
        self.plan_waits(1000)
        self.plan_waits(1001, "Which database?")
        self.assertEqual(self.unread_items(), ["- Which database?"])
        self.plan_waits(1021, "Merge or not?")
        self.assertEqual(self.unread_items(), ["- Merge or not?"])

    def test_a_waiting_item_added_within_a_minute_stays_unread_once_the_first_is_answered(self):
        self.plan_waits(1000)
        self.plan_waits(1001, "A")
        self.plan_waits(1011, "A", "B")
        self.plan_waits(1021, "B")                          # A answered, B still waits
        self.assertTrue(any("- B" in (i or "") for i in self.unread_items()), self.n.items)

    def test_only_the_newest_notices_are_kept_in_memory(self):
        for k in range(pilot.KEEP_NOTICES + 5):
            self.note(f"n{k}", "quiet", f"q{k}", "leg:a", read=True)
        self.update(1000)
        self.assertEqual(len(self.n.items), pilot.KEEP_NOTICES)
        self.assertEqual(self.n.items[0]["id"], "n5")

    def test_another_session_or_another_text_is_not_folded(self):
        self.note("t1", "turn", "pilot pp ended its turn, nothing in the air", "pilot:P", at=1000)
        fresh, folded = self.n.fold([{"id": "t2", "at": 1010, "kind": "turn", "worker": "b", "sessionId": "s-b",
                                      "text": "pilot pp ended its turn, nothing in the air"},
                                     {"id": "t3", "at": 1010, "kind": "turn", "worker": "a", "sessionId": "s-a",
                                      "text": "pilot pp ended its turn, nothing in the air: more"}], 1010)
        self.assertEqual(([f["id"] for f in fresh], folded), (["t2", "t3"], 0))

    # ---- deterministic rules

    def test_a_later_done_supersedes_the_legs_earlier_done_blocked_and_turn_ended_but_not_a_failure(self):
        self.leg("a", "done")
        self.note("t1", "turn", "a: turn ended", "leg:a", head="turn")
        self.note("t2", "failed", "a: FAILED", "leg:a", head="FAILED")
        self.note("t3", "waiting", "a: BLOCKED", "leg:a", head="BLOCKED")
        self.note("t4", "done", "b: DONE", "leg:b", head="DONE", worker="b")
        self.note("t5", "done", "a: DONE", "leg:a", head="DONE")
        self.assertEqual(self.settle(), 2)
        self.assertEqual([(i["id"], i["read"], i.get("settled")) for i in self.n.items],
                         [("t1", True, "superseded by t5"), ("t2", False, None), ("t3", True, "superseded by t5"),
                          ("t4", False, None), ("t5", False, None)])
        self.assertEqual(self.by("t1")["settledBy"], "t5")

    def test_a_later_blocked_supersedes_an_earlier_done(self):
        self.leg("a", "done")
        self.note("t1", "done", "a: DONE", "leg:a", head="DONE")
        self.note("t2", "waiting", "a: BLOCKED", "leg:a", head="BLOCKED")
        self.settle()
        self.assertEqual((self.by("t1")["settled"], self.by("t2")["read"]), ("superseded by t2", False))

    def test_notices_from_before_subjects_are_read_from_their_wording(self):
        self.leg("a", "done")
        old = {"id": "t1", "at": 1, "kind": "done", "worker": "a", "sessionId": "s-a", "text": "a: DONE (x)", "read": False, "href": None}
        self.n.items.append(old)
        self.note("t2", "done", "a: DONE", "leg:a", head="DONE")
        self.settle()
        self.assertEqual(old["settled"], "superseded by t2")
        self.assertEqual(pilot.notice_subject({"worker": "pp", "text": "pilot pp is waiting on you: x", "href": "#/s/P"}), "pilot:P")
        self.assertEqual(pilot.notice_subject({"worker": "Notices", "text": "Notices: the pilot is waiting on you", "href": "#/s/P"}), "plan:P")

    def test_a_waiting_item_settles_once_it_leaves_the_plans_waiting_section(self):
        self.waiting("s1", "Which database?", "Ship on Friday?")
        self.note("t1", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Which database?\n- Ship on Friday?")
        self.settle()
        self.assertFalse(self.by("t1")["read"])
        self.waiting("s1", "Ship on Friday?")             # one answered, one still there
        self.settle()
        self.assertFalse(self.by("t1")["read"])
        self.waiting("s1", "Something new")
        self.settle()
        self.assertEqual(self.by("t1")["settled"], "answered")

    def test_a_later_plan_notice_listing_the_same_items_supersedes(self):
        self.waiting("s1", "Which database?", "Ship on Friday?")
        self.note("t1", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Which database?")
        self.note("t2", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Which database?\n- Ship on Friday?")
        self.settle()
        self.assertEqual(self.by("t1")["settled"], "superseded by t2")
        self.assertFalse(self.by("t2")["read"])

    def test_a_question_settles_once_the_pilot_or_leg_moved_on(self):
        self.snap["pilots"] = [{"sessionId": "P", "status": "waiting"}]
        self.leg("a", "blocked")
        self.leg("b", "done")
        self.note("t1", "waiting", "pilot pp is waiting on you: merge?", "pilot:P", worker="pp")
        self.note("t2", "waiting", "a is waiting on you", "leg:a", head="ASK")
        self.note("t3", "waiting", "b: BLOCKED", "leg:b", head="BLOCKED", worker="b")
        self.settle()
        self.assertEqual([i["read"] for i in self.n.items], [False, False, False])
        self.snap["pilots"][0]["status"] = "busy"
        self.leg("a", "working")
        self.leg("b", "working")
        self.settle()
        self.assertEqual([i.get("settled") for i in self.n.items], ["answered"] * 3)

    def test_a_merge_question_settles_once_its_pr_merged(self):
        self.waiting("s1", "Merge web-app #8?")
        self.note("t1", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Merge web-app #8?")
        self.prs[("web-app", 8)] = "OPEN"
        self.settle()
        self.assertFalse(self.by("t1")["read"])
        self.prs[("web-app", 8)] = "MERGED"
        self.settle()
        self.assertEqual(self.by("t1")["settled"], "merged")

    def test_a_plan_notice_with_a_merge_and_an_open_question_waits_for_both(self):
        self.waiting("s1", "Merge web-app #8?", "Which database?")
        self.note("t1", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Merge web-app #8?\n- Which database?")
        self.prs[("web-app", 8)] = "MERGED"
        self.settle()
        self.assertFalse(self.by("t1")["read"])

    def test_settling_through_update_is_saved(self):
        self.leg("a", "done")
        self.update(1000)
        self.note("t1", "done", "a: DONE", "leg:a", head="DONE")
        self.note("t2", "done", "a: DONE", "leg:a", head="DONE")
        self.update(1001)
        saved = json.loads(pilot.NOTICES.read_text(encoding="utf-8"))["items"]
        self.assertEqual([(i["id"], i.get("settled")) for i in saved], [("t1", "superseded by t2"), ("t2", None)])

    # ---- the guard

    def test_the_guard_keeps_failures_waiting_items_and_merge_questions(self):
        self.waiting("s1", "Which database?", "Merge web-app #8?")
        self.prs[("web-app", 8)] = "OPEN"
        self.snap["pilots"] = [{"sessionId": "P", "status": "waiting"}]
        self.leg("a", "done")
        kept = [self.note("f1", "failed", "a: FAILED", "leg:a", head="FAILED"),
                self.note("f2", "died", "a stopped on an error: 529", "leg:a"),
                self.note("f3", "budget", "a reached its budget and was told to report", "leg:a"),
                self.note("f4", "turn", "a: the budget raise was refused", "leg:a"),
                self.note("w1", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Which database?"),
                self.note("w2", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Merge web-app #8?"),
                self.note("w3", "waiting", "pilot pp is waiting on you: merge web-app #8?", "pilot:P", worker="pp"),
                self.note("w4", "waiting", "a: BLOCKED", "leg:a", head="BLOCKED"),
                self.note("w5", "waiting", "Old: the pilot is waiting on you", "plan:gone", items="- x")]
        ctx = pilot.notice_context(self.snap, kept)
        self.assertEqual({n["id"]: pilot.still_needed(n, ctx) for n in kept},
                         {"f1": "a failure", "f2": "a failure", "f3": "a failure", "f4": "a failure",
                          "w1": "an item still waiting on the controller", "w2": "an unanswered merge question",
                          "w3": "an unanswered merge question", "w4": "still waiting on the controller",
                          "w5": "an item still waiting on the controller"})
        free = [self.note("q1", "quiet", "a has written nothing for 30 minutes", "leg:a"),
                self.note("q2", "waiting", "pilot zz is waiting on you: x", "pilot:Z", worker="zz")]   # its session is gone
        self.assertEqual([pilot.still_needed(n, ctx) for n in free], [None, None])

    # ---- Haiku

    def judge_with(self, answer, now=10_000):
        prompts = []

        def fake(prompt, timeout=150):
            prompts.append(prompt)
            if isinstance(answer, Exception):
                raise answer
            return {"result": answer}
        with unittest.mock.patch.object(pilot, "haiku", fake), \
                unittest.mock.patch.object(pilot, "claude", lambda *a, **k: self.fail("the real claude was called")):
            asked = self.n.judge(self.snap, now=now)
        return asked, prompts

    def judged_setup(self):
        self.waiting("s1", "Which database?")
        self.prs[("web-app", 8)] = "OPEN"
        self.leg("a", "done")
        self.note("t1", "quiet", "a has written nothing for 30 minutes", "leg:a", at=100)
        self.note("t2", "done", "a: DONE", "leg:a", head="DONE", at=200)
        self.note("t3", "turn", "b: turn ended", "leg:b", head="turn", worker="b", at=210)
        self.note("t4", "quiet", "c has written nothing", "leg:c", read=True, worker="c", at=220)
        self.note("t5", "waiting", "Notices: the pilot is waiting on you", "plan:s1", items="- Which database?", at=230)
        self.note("t6", "failed", "c: FAILED", "leg:c", head="FAILED", worker="c", at=240)
        self.note("t7", "waiting", "pilot pp is waiting on you: merge web-app #8?", "pilot:P", worker="pp", at=250)
        self.snap["pilots"] = [{"sessionId": "P", "name": "pp", "status": "waiting"}]

    def test_haiku_acks_the_ids_it_names_with_its_reasons(self):
        self.judged_setup()
        asked, prompts = self.judge_with("t1 | superseded by t2\nt3 | b was removed; nothing left to do\n")
        self.assertTrue(asked)
        self.assertEqual((self.by("t1")["settled"], self.by("t1")["settledBy"], self.by("t1")["settledByJudge"]),
                         ("superseded by t2", "t2", True))
        self.assertEqual((self.by("t3")["read"], self.by("t3")["settled"]), (True, "b was removed; nothing left to do"))
        self.assertNotIn("settledBy", self.by("t3"))
        self.assertFalse(self.by("t2")["read"])
        self.assertIn("t1 | ", prompts[0])
        self.assertNotIn("t4 | ", prompts[0])               # read already: not asked about
        self.assertIn("- leg a: done", prompts[0])
        self.assertIn("waiting on the controller: which database?", prompts[0])
        self.assertIn("- web-app #8: OPEN", prompts[0])
        self.assertEqual(json.loads(pilot.NOTICES.read_text(encoding="utf-8"))["items"][0]["settled"], "superseded by t2")

    def test_unknown_and_already_read_ids_are_ignored(self):
        self.judged_setup()
        self.judge_with("zz | no such notice\nt4 | already read\nt1 | stale\nt1 | twice")
        self.assertEqual(self.by("t1")["settled"], "stale")
        self.assertNotIn("settled", self.by("t4"))
        self.assertFalse(any(i.get("id") == "zz" for i in self.n.items))

    def test_the_guard_drops_what_haiku_names_that_still_needs_the_controller(self):
        self.judged_setup()
        self.judge_with("t5 | the pilot will decide\nt6 | it was retried\nt7 | superseded by t2\nt1 | superseded by t2")
        self.assertEqual([self.by(i)["read"] for i in ("t5", "t6", "t7")], [False, False, False])
        self.assertTrue(self.by("t1")["read"])

    def test_a_failed_slow_or_malformed_call_acks_nothing(self):
        for answer in (RuntimeError("claude exited 1"), subprocess.TimeoutExpired("claude", 90),
                       "Sure! t1 can go.", "", "t1 | superseded by t2\nand t3 too"):
            pilot.NOTICES.unlink(missing_ok=True)
            self.n = pilot.Notices()
            self.n.spawn = lambda fn: fn()
            self.judged_setup()
            asked, _ = self.judge_with(answer)
            self.assertTrue(asked)
            self.assertEqual([i["id"] for i in self.n.items if i["read"]], ["t4"], answer)
            self.assertFalse(self.n.judging)
        self.assertEqual(pilot.parse_judgement("NONE"), [])
        self.assertEqual(pilot.parse_judgement("```\nt1 | stale\n```"), [("t1", "stale")])

    def test_haiku_is_asked_only_when_unread_changed_and_at_most_once_a_minute(self):
        self.judged_setup()
        self.assertTrue(self.judge_with("NONE", now=10_000)[0])
        self.assertFalse(self.judge_with("NONE", now=10_100)[0])     # nothing changed
        self.note("t8", "quiet", "d has written nothing", "leg:d", worker="d", at=300)
        self.assertFalse(self.judge_with("NONE", now=10_030)[0])     # changed, but within the minute
        self.assertTrue(self.judge_with("NONE", now=10_061)[0])
        self.note("t9", "quiet", "e has written nothing", "leg:e", worker="e", at=310)
        self.n.judging = True                                        # a call still in flight
        self.assertFalse(self.judge_with("NONE", now=10_200)[0])
        self.n.judging = False
        for i in self.n.items:
            i["read"] = True
        self.assertFalse(self.judge_with("NONE", now=10_300)[0])     # nothing unread: nothing to ask

    def test_its_own_acks_do_not_count_as_a_change(self):
        self.judged_setup()
        self.judge_with("t1 | stale", now=10_000)
        self.assertFalse(self.judge_with("NONE", now=10_100)[0])

    def test_the_judge_never_runs_in_the_watchers_thread(self):
        started = []
        n = pilot.Notices()
        n.spawn = started.append
        n.items.append({"id": "t1", "at": 1, "kind": "quiet", "worker": "a", "text": "x", "read": False})
        with unittest.mock.patch.object(pilot, "haiku", lambda *a, **k: self.fail("asked in the watcher's thread")):
            self.assertTrue(n.judge(self.snap, now=10_000))
        self.assertEqual(len(started), 1)


if __name__ == "__main__":
    unittest.main()
