#!/usr/bin/env bash
# PreToolUse hook (wire with matcher "" -- every tool, coordinator included):
# the only one of the three token-discipline hooks that PREVENTS spend rather
# than measuring it. Two things it guards, both "read this whole giant file
# straight into context":
#
#   1. The Read tool, when called with none of `limit`, `offset` or
#      `line_range` -- i.e. a request for the WHOLE file.
#   2. A Bash call whose ENTIRE command is a bare `cat <one file>` with
#      nothing else in it -- see the narrow-match note below.
#
# Applies to the coordinator's own calls too, not just subagents: a whole-file
# read is expensive whoever makes it. There is no agent_id gate in this hook.
#
# It never denies SubagentHandback or SendMessage -- checked first, before
# anything else -- on the same principle as the other two hooks even though
# neither call carries a file path to check.
#
# Threshold: CLAUDE_BIG_READ_THRESHOLD_BYTES, default 100000 bytes.
#
# Measured 2026-09-16 against ~/conditionsapply, the project whose
# own CLAUDE.md names Program.cs as a file that must never be read whole --
# this is the measurement population this number is grounded in, not a
# requirement that every installation resemble it. Program.cs is now
# 569,773 bytes, 9,952 lines (grown from 564,558 bytes / 9,881 lines since
# this hook was first written; the project deploys continuously, so this
# will keep drifting upward).
#
# n=293 real .cs source files under conditionsapply/src (bin/obj build
# output and .claude/worktrees subagent-worktree duplicates excluded, since
# those are copies of the same tree, not distinct project files): median
# 7,191 bytes, p90 22,934, p95 32,591, max 569,773 (Program.cs itself).
# Scoped to just ConditionsApply.Service, the narrower population this hook
# was originally measured against (n=169): p90 is 27,621 bytes, matching
# the figure this hook first shipped with exactly.
#
# 100,000 sits comfortably above every ordinary file (roughly 3.6x to 4.4x
# above p90, depending on scope) and blocks exactly one file in the whole
# tree, Program.cs, 0.341% of the 293. That is still the design goal this
# threshold hits, unchanged from when it was first set.
#
# THE BASH GUARD IS DELIBERATELY NARROW. A general "detect a big cat in an
# arbitrary bash command" is not attempted -- that needs a real shell parser
# to avoid false positives on pipelines (`cat f | jq .`), filters, heredocs
# (whose BODY TEXT can itself contain the literal string "cat somefile" as
# data, not as a command), multi-command lines (`x; cat f; y`), and command
# substitution. Regex heuristics for any of those either miss real cases or
# wrongly block a legitimate compound command -- and a guard that blocks
# legitimate commands is worse than no guard. What IS shipped here only fires
# when the trimmed command string is, in its entirety, a bare `cat` of
# exactly one plain-looking argument (optionally via `command ` or a full
# path) -- no pipe, redirect, `;`, `&&`, `||`, heredoc marker, glob or
# variable-expansion character anywhere in the string. That anchoring is what
# makes it safe: every one of the named false-positive shapes contains a
# character the pattern does not allow, so none of them can match. Everything
# else silently falls through unguarded (false negatives, by design).
#
# Safety: set -u (not -e). Fails OPEN on a stat that fails, a path that does
# not exist, empty/unparseable stdin, or any jq failure.
set -u

THRESHOLD="${CLAUDE_BIG_READ_THRESHOLD_BYTES:-100000}"
[[ "$THRESHOLD" =~ ^[0-9]+$ ]] || THRESHOLD=100000

input="$(cat 2>/dev/null)"
[ -z "$input" ] && exit 0

tool_name="$(printf '%s' "$input" | jq -r '.tool_name // empty' 2>/dev/null)"
[ $? -ne 0 ] && exit 0
[ -z "$tool_name" ] && exit 0

# Never denied, on principle, even though neither carries a file path.
case "$tool_name" in
    SubagentHandback|SendMessage)
        exit 0
        ;;
esac

deny_json() {
    # $1 = file path, $2 = size in bytes, $3 = how-to-proceed hint
    jq -n --arg path "$1" --arg size "$2" --arg hint "$3" '{
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "deny",
        permissionDecisionReason: ("\($path) is \($size) bytes -- too large to read whole. \($hint)")
      }
    }'
}

if [ "$tool_name" = "Read" ]; then
    limit="$(printf '%s' "$input" | jq -r '.tool_input.limit // empty' 2>/dev/null)"
    offset="$(printf '%s' "$input" | jq -r '.tool_input.offset // empty' 2>/dev/null)"
    line_range="$(printf '%s' "$input" | jq -r '.tool_input.line_range // empty' 2>/dev/null)"
    # Any of these present means the caller already asked for a slice, not
    # the whole file -- nothing to guard.
    if [ -n "$limit" ] || [ -n "$offset" ] || [ -n "$line_range" ]; then
        exit 0
    fi

    file_path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty' 2>/dev/null)"
    [ -z "$file_path" ] && exit 0

    size="$(stat -c%s -- "$file_path" 2>/dev/null)"
    [[ "$size" =~ ^[0-9]+$ ]] || exit 0   # stat failed / doesn't exist: fail open

    if [ "$size" -gt "$THRESHOLD" ]; then
        deny_json "$file_path" "$size" "Retry with a line range -- e.g. offset/limit (read it in a few hundred-line slices), or search it first with Grep and read only the matching ranges."
        exit 0
    fi
    exit 0
fi

if [ "$tool_name" = "Bash" ]; then
    command_str="$(printf '%s' "$input" | jq -r '.tool_input.command // empty' 2>/dev/null)"
    [ -z "$command_str" ] && exit 0

    # Trim trailing whitespace/newlines only -- leading/internal whitespace is
    # still significant for the anchor match below.
    trimmed="$(printf '%s' "$command_str" | sed -e 's/[[:space:]]*$//')"

    # Bare `cat <one plain argument>`, optionally via `command ` or a full
    # path, and NOTHING else -- see header comment for why this is safe.
    if [[ "$trimmed" =~ ^(command[[:space:]]+)?(/usr/bin/|/bin/)?cat[[:space:]]+(\"[^\"\$\`]*\"|\'[^\']*\'|[A-Za-z0-9_./-]+)[[:space:]]*$ ]]; then
        arg="${BASH_REMATCH[3]}"
        # Strip matching surrounding quotes, if any.
        if [[ "$arg" == \"*\" || "$arg" == \'*\' ]]; then
            arg="${arg:1:${#arg}-2}"
        fi
        size="$(stat -c%s -- "$arg" 2>/dev/null)"
        if [[ "$size" =~ ^[0-9]+$ ]] && [ "$size" -gt "$THRESHOLD" ]; then
            deny_json "$arg" "$size" "Use sed -n 'START,ENDp' <file>, or head/tail with a line count, instead of a bare cat of the whole file."
            exit 0
        fi
    fi
    exit 0
fi

exit 0
