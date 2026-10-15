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

## Shells on Windows

- **`python`, never `python3`**: on Windows the name `python3` is the Microsoft Store alias and fails
  with "Python was not found".
- **`pwsh`, never `powershell`**: `powershell` is Windows PowerShell 5.1.

## Windows and consoles

The controller, 2026-10-15: "any window that opens and that can't use -NoWindow has to show what is
going on in stderr/stdout".

- **Background work opens no window.** Windows Terminal, the default terminal on Windows 11, ignores
  `-WindowStyle Hidden`: `pwsh -WindowStyle Hidden` from a scheduled task or `Start-Process` still opens
  a window, empty. A deconvolution grid's scheduled task opened one each time it started. Use
  `Start-Process -NoNewWindow`, `subprocess` with `CREATE_NO_WINDOW`, or, in a scheduled task,
  `conhost.exe --headless pwsh.exe -NoProfile -File ...`.
- **A window that has to open shows what is going on.** A GUI under test, or a tool that needs its own
  console: its stdout and stderr go to that window, not only to a log file behind an empty one, so
  whoever sees it can tell what it is, who started it and whether it is stuck. Give it a title that
  names it where you can.
