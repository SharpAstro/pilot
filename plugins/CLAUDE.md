# pilot's house rules

The controller's rules for every pilot and worker session, wherever pilot is installed. Both plugins'
SessionStart hooks put this file into the session's context (`pilot.py hook-rules`), at its start and
again after a compaction, so a session behaves the same on any machine whatever that machine's own
`~/.claude/CLAUDE.md` says. A machine's file adds what is its own (hardware, paths, installed tools); it
does not loosen these.

## Writing

- **No em-dashes**, and the `--` substitute is no better: the problem is the habit of bolting an
  appositive onto every sentence, not the character. Use a full stop, a comma, a semicolon or
  parentheses. In prose, comments, commit messages, and above all in GLSL, where a non-ASCII byte in a
  shader source breaks its compilation.

## Review feedback

The controller, 2026-10-17: "If we have review feedback, it needs to go onto the branch that is being
reviewed, not as follow up. Follow up is really just a synonym for fuck up."

- **Every review finding that is acted on is fixed on the branch under review, before the merge is asked
  for**: bugs, a question that turns out a defect, and any noted or preference item that is taken. Push the
  fixes to that PR. Past the review rounds the fixes are still made there, checked by the pilot.
- **Never a follow-up** for review feedback: no follow-up issue, leg, PR or flight, and never a merge asked
  for with feedback unfixed. What is not taken is declined in the plan's log, with why. A finding that is
  not the PR's at all goes to the controller, never filed on a session's own say.

## Shells on Windows

- **`python`, never `python3`**: on Windows the name `python3` is the Microsoft Store alias and fails
  with "Python was not found".
- **`pwsh`, never `powershell`**: `powershell` is Windows PowerShell 5.1.

## Windows and consoles

The controller, 2026-10-15: "any window that opens and that can't use -NoWindow has to show what is
going on in stderr/stdout".

- **Background work opens no window, and there is one proper way to start it.** Windows Terminal is the
  default terminal on Windows 11: a console program started without a console of its own (from a
  scheduled task, or `Start-Process` without `-NoNewWindow`) gets a Windows Terminal window, and
  `-WindowStyle Hidden` does not hide it, so it sits there empty. A deconvolution grid's scheduled task
  opened one each time it started. So:
  - **From the session itself**, which is how background work runs: its background shell, or a child
    that shares the session's console. `Start-Process -NoNewWindow` in PowerShell, or
    `subprocess.Popen(..., creationflags=subprocess.CREATE_NO_WINDOW)` in Python. This is the way.
  - **Not as a scheduled task.** If one is truly needed, its action goes through
    `conhost.exe --headless <program> ...` (a console with no window), and its `Settings.Priority` is
    raised to 4, normal. Task Scheduler runs an action at 7, below normal, and on a busy machine its
    child got almost no CPU: a task's Python looked hung for an hour while eight normal-priority runs
    held the CPU at 100%.
  - **Check it with the real program.** Before relying on a way of starting something, start the very
    program it will run that way and watch it do work. A check that ran only PowerShell commands passed
    while the Python the task started got no CPU.
- **A process is not hung while the CPU is saturated.** Before calling one hung, see whether its CPU
  time moves and how busy the machine is.
- **A window that has to open shows what is going on.** A GUI under test, or a tool that needs its own
  console: its stdout and stderr go to that window, not only to a log file behind an empty one, so
  whoever sees it can tell what it is, who started it and whether it is stuck. Give it a title that
  names it where you can.
