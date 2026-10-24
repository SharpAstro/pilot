"""PreToolUse hook: refuses an edit that makes pilot's UI wait (a lock, a blocking wait on a task, a sleep).

The UI's code (src/Pilot.Ui, src/Pilot.Gui, src/Pilot.Tui) is the render thread's, or shares state with it, and the render
thread never waits on anyone: a window froze on SE1 (2026-10-24) behind locks and work done inline. State that crosses
threads is an immutable record swapped whole (Volatile.Read/Write, ImmutableInterlocked); work that takes time goes to the
pool (tracker.Run, Task.Run). The same rule is a test (LayoutDrivenTests.NothingInTheUiWaits); this hook says it at the
edit, before a build.

Exit 2 blocks the edit and hands the reason to Claude; anything else lets it through.
"""
import json
import re
import sys

UI = re.compile(r"[\\/]src[\\/]Pilot\.(Ui|Gui|Tui)[\\/].*\.cs$")
WAITS = re.compile(
    r"\block\s*\(|\.Wait\s*\(|\.Result\b|GetAwaiter\s*\(\s*\)\s*\.GetResult|Thread\.Sleep|\bSemaphoreSlim\b|\bMonitor\.|ReaderWriterLock|\.WaitAll\s*\("
)
# Strings and comments blanked, so a word in a string or a doc comment is not code (as the test does).
LITERAL = re.compile(r"'(?:[^'\\\n]|\\.)'|@?\$?\"(?:[^\"\\\n]|\\.)*\"|//[^\n]*|/\*.*?\*/", re.S)


def main() -> int:
    try:
        hook = json.load(sys.stdin)
    except ValueError:
        return 0
    tool = hook.get("tool_input") or {}
    path = tool.get("file_path") or ""
    if not UI.search(path):
        return 0
    texts = [tool.get("content"), tool.get("new_string")] + [e.get("new_string") for e in tool.get("edits") or []]
    found = sorted({m.group(0) for t in texts if t for m in WAITS.finditer(LITERAL.sub(" ", t))})
    if not found:
        return 0
    print(
        f"pilot's UI never waits, and this edit to {path} adds {', '.join(found)}. The render thread must never block: "
        "keep state that crosses threads as an immutable record swapped whole (Volatile.Read/Write, ImmutableInterlocked.Update), "
        "and run anything that takes time on the pool (tracker.Run, Task.Run) instead of waiting for it.",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    sys.exit(main())
