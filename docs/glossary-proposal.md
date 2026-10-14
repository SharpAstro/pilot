# Glossary — PROPOSAL

> **This is a proposal, not the vocabulary in force.** Nothing has been renamed. The controller picks a
> word per row; a later leg (terms-rename) applies the choices. Counts are whole-word, case-insensitive
> matches in README.md, plugins/pilot/skills/pilot/SKILL.md, plugins/pilot/agents/*.md, pilot.py and
> web/ (app.js, index.html), taken on top of d53620b. pilot.py's counts include comments and docstrings.

## The table

*Compat* says whether the proposal touches a CLI command or flag, a file name, a JSON key or value, or an
API path. **none** means only prose, help text and UI labels change.

| Term | Means | Proposed word | Other words in use (to retire) | Compat impact |
|---|---|---|---|---|
| The person | You: you answer what is waiting, merge, set budgets, stop and archive | **controller** (183: README.md:42, SKILL.md:8, pilot.py:3470) | *the tower* (38: README.md:42, SKILL.md:143, copilot.md:11), *flight controller* (1: SKILL.md:8), *the person who runs the work* (4: copilot.md:11, pilot.py:2641), *you* | The PR stage key `"tower"` (pilot.py:1421) is a JSON value in `/api/state`. Keep the key and change only its label "to the tower" → "to the controller". Otherwise none. |
| The controller's stand-in | Opus session answering the waiting items while you are away | **autopilot** (120: README.md:187, pilot.py:73) | — | none |
| The refuel approver | One Haiku call that approves a refuel or passes it to the controller | **watchtower** (30: README.md:261, pilot.py:2718), or **dispatcher** if *tower* is retired (in aviation, the dispatcher signs off the fuel) | *the refuel gates* is its no-model pre-checks (README.md:258); keep that as **gates** | Renaming it changes the black box's `who` value `watchtower` (stored in blackbox.jsonl; filter at app.js:1434). |
| The refuel switch | auto (watchtower may approve) / manual (all to the controller) | **auto / manual** for the stored values. Label it "watchtower may approve / all to me" | *auto* clashes with *autopilot* and Claude Code's *auto mode* (app.js:376-382, README.md:266) | Stored in refuel.json and `/api/refuel`. Change only the UI label. |
| The planning session | The Claude session that keeps the plan and starts legs | **pilot** (≈690) | It also names the product (README.md:1, index.html:6), the pwsh function (README.md:57) and pilot.py as a refuser ("pilot's budget", README.md:132). In prose, write **pilot.py** for the tool. | none |
| Second-opinion subagent | Read-only Sonnet subagent | **co-pilot** in prose | *copilot* (5) only as the identifier | `copilot` is the subagent type and the file `agents/copilot.md`. Keep it. |
| Background session flying a leg | The `claude --bg -w` session | **worker** (331) | — | none |
| One piece of work | A row on the route, flown by one worker of the same name | **leg** (≈650) | — | none |
| The whole undertaking | Objective, legs, flight number | **flight** (≈394) | *plan* when it means the flight: "plan card" (7: README.md:86, app.js:955), "plan id" | none. A rename to **flight card** is UI and prose only. |
| The Markdown file | Objective, waiting section, notes, route, log | **flight plan**, or **plan** for short (≈480) | — | none (`pilot plan`, `plans/`) |
| The legs table | `## Route` | **route** (112) | *routing* for the autopilot's dispatch (README.md:196) | none |
| A backup leg / taking it | A leg in *Alt of*; `start --divert` | **alternate** / **divert**. These are two different things, both clear. | — | none |
| Flight dependency | Another flight whose merge this one waits for | **connection** (`connect`, `Connects:`) | *connection* also means the dashboard's server link (index.html:21 "connection to the dashboard server", "connecting…"). Call that one **stream**. | none (UI label). Flight side: the JSON key `connections` stays. |
| Leg finished, checklist complete | DONE, nothing open, nothing uncommitted | **done** (the DONE report; the Overview table's state, README.md:131) | *landed* for a leg (≈100: pilot.py:801, SKILL.md:151, README.md:22). *Landed* also means a flight whose PRs are all merged (pilot.py:3074), and a leg "lands" *before* its PR goes "on approach" and gets "cleared to land". | **Yes.** `"landed"` is a leg-time state (pilot.py:802) and a key in `connections[]` (pilot.py:722). Both are in `/api/state` and the tests. |
| PR merged | The last review stage | **landed** (the flight is landed when every leg has landed) | *at the gate* (37: pilot.py:1422, SKILL.md:205) | Stage key `"gate"` (pilot.py:1421). Keep the key, change the label. |
| PR review stages | Round due / fixing / CI running / rounds spent | **on approach / go-around / holding / to the controller** | *go around* is unused. *Holding* also names the plan-only hold (pilot.py:3388). | none (labels; keys `approach`, `goaround`, `holding`, `tower` stay) |
| PR accepted and green, waiting on the team | Team review pending | **team review** | *awaiting clearance* (pilot.py:1428, SKILL.md:195), which also names the plan-only hold | none (label; key `clearance` stays) |
| PR ready for your merge | Accepted, CI green, team approved | **cleared to land** (8) | — | none |
| Plan-only flight | Launched to plan, no leg may start until cleared | **held at the gate, awaiting clearance**. *At the gate* and *awaiting clearance* then mean only this. | *holding* for this state (pilot.py:3276, README.md:100) | `Hold: awaiting clearance` is a plan-file line (pilot.py:414) and `--hold`/`clear` are CLI. Keep all three. |
| Lifting the hold | `pilot clear` / the card button | **cleared for takeoff** (9) | — | none |
| Removing a worker | `pilot rm`: sessions, worktree and branch go, record kept | **removed** (29) | *archive* for a worker (SKILL.md:228 "still archives", pilot.py:3509, app.js:1456) | The `archive/` folder and the `archivedAt` key are stored state. Keep them; change prose only. |
| Shelving a flight | `pilot archive` | **archive** (≈94) | *shelved* (internal, pilot.py:698) | none |
| Messaging a live session | `pilot tell`, the dashboard's message box | **intercom** (35) | *relay* (12: SKILL.md:35, pilot.py:2512, app.js:665). `pilot-dashboard` is the session that carries it. | `pilot-dashboard` is a session name pilots are told to expect (SKILL.md:35, 147). Keep it. `/api/intercom` stays. |
| A dashboard alert | One entry in the bell | **notice** (79) | *notification* (24: index.html:25 "Notifications", README.md:139) | `notifications.json` and `/api/notifications` are file/API names. Keep them; change the UI heading to "Notices". |
| The dashboard | `pilot dashboard` | **dashboard** (119) | *radar* (1: README.md:42) | none |
| A flight on the Flights panel | One row with its legs as stops | **strip** (≈6 real uses: pilot.py:716, app.js:1678) | — | none |
| A plan on the Overview | The unfolding plan block | **card** (with Terminal card and Subagents card) | — | none |
| Decision log | blackbox.jsonl, Black box page | **black box** (15) | — | none |
| Token ceiling / raising it | `budget` (controller's override) / `refuel` (pilot's request) | **budget** for the number, **refuel** for a raise | *fuel* (11: README.md:132 "Fuel does not wake", SKILL.md:206 "Fuel goes through"), except the ⛽ icon and "tank" | none |

## Renames that are compatibility changes

Only these touch a CLI command or flag, a file name, a JSON key or value, or an API path. Every other row
changes prose, help text and UI labels only.

1. **leg "landed" → "done"**, if the keys move with the word. Affected: the leg-time state value `"landed"`
   (pilot.py:802) and `connections[].landed` (pilot.py:722) in `/api/state`, read by app.js and the tests.
   It can be done label-only, keeping the keys.
2. **watchtower → dispatcher**. Affected: the black box's `who` value, which is already stored in
   blackbox.jsonl, and the Black box filter list (app.js:1434). Old rows would need mapping on read.
3. **PR stage keys** `gate`, `clearance`, `tower`. The proposal keeps them and changes only their labels;
   renaming them changes `/api/state`.
4. **Things this proposal keeps on purpose**, because renaming them would break something:
   - the `copilot` subagent type and file;
   - the `pilot-dashboard` session name;
   - the `Hold: awaiting clearance` plan line, `plan --hold` and `clear`;
   - `notifications.json` and `/api/notifications`;
   - the `archive/` folder and `archivedAt` key for removed workers;
   - refuel.json's `auto`/`manual`.

## Recommendation

Make one word mean one thing, and keep aviation order. Today a leg "lands" when its checklist is done
(pilot.py:801), and only afterwards does its PR go "on approach" and get "cleared to land". "At the gate",
"holding" and "awaiting clearance" each mean both a PR stage and the plan-only hold.

The fix:
- A finished leg is **done**, which matches its DONE report.
- **Landed** means merged, so a flight lands when every leg has landed. That is already how
  `flight_landed` decides it.
- The PR runs **on approach → go-around → holding → (team review) → cleared to land → landed**.
- **At the gate, awaiting clearance** belongs only to a flight not yet cleared for takeoff, which is where
  aviation puts the gate.
- Retire *tower* for **controller**. With *tower* gone, *watchtower* has no partner, so rename it
  **dispatcher**, or keep it if the black-box value matters more.
- Retire *relay*, *radar*, *fuel* as a noun, and *archive* for a removed worker.

Do all of this as labels and prose first, keeping every key, file and API path, so terms-rename touches no
stored state.
