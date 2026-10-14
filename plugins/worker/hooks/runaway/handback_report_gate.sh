#!/usr/bin/env bash
# handback_report_gate.sh -- PreToolUse hook, matched on SubagentHandback.
#
# A staff agent's SubagentHandback message IS its report: the coordinator is
# told to read it there, and the tool result does not repeat it. In the seven
# days to 2026-10-13, 14 of 2,634 handbacks carried the literal message
# "placeholder", 13 of them sent in the same message as a last Bash call whose
# result the agent had not yet seen. The coordinator got the word
# "placeholder" as the whole report; the mission-08 review and a
# mission-runner's product owner were caught only because their coordinators
# checked the files on disk themselves.
#
# So a handback whose message is shorter than MIN_CHARS (or one of the filler
# words below) is refused, with the reason. Every staff report carries
# Citations and Assumptions sections, so no real report is that short.
#
# The refusal has a ceiling, because this tool is the report path: a gate that
# refused it forever is what looped seven agents on a refused report for about
# 320k tokens. After MAX_DENIALS refusals for one agent, the next handback goes
# through whatever it says.
#
# Fail-open: no jq, no input, no agent_id, any parse failure -> allow.
# Off switch: CLAUDE_HANDBACK_GATE_OFF=1.

[ -n "${CLAUDE_HANDBACK_GATE_OFF:-}" ] && exit 0
command -v jq >/dev/null 2>&1 || exit 0
input=$(cat 2>/dev/null) || exit 0
[ -z "$input" ] && exit 0

tool_name="$(printf '%s' "$input" | jq -r '.tool_name // ""' 2>/dev/null)" || exit 0
[ "$tool_name" = "SubagentHandback" ] || exit 0

MIN_CHARS="${CLAUDE_HANDBACK_MIN_CHARS:-40}"
MAX_DENIALS=2
STATE_DIR="${CLAUDE_HANDBACK_STATE_DIR:-${HOME}/.claude/state/handback-gate}"

msg="$(printf '%s' "$input" | jq -r '.tool_input.message // ""' 2>/dev/null)" || exit 0
trimmed="$(printf '%s' "$msg" | tr -s '[:space:]' ' ' | sed 's/^ //; s/ $//')"
lower="$(printf '%s' "$trimmed" | tr '[:upper:]' '[:lower:]')"

short=0
[ "${#trimmed}" -lt "$MIN_CHARS" ] && short=1
case "$lower" in placeholder|placeholder.|tbd|todo|"..."|"…"|report|done|done.) short=1 ;; esac
[ "$short" = 1 ] || exit 0

session_id="$(printf '%s' "$input" | jq -r '.session_id // "nosession"' 2>/dev/null)"
agent_id="$(printf '%s' "$input" | jq -r '.agent_id // empty' 2>/dev/null)"
[ -n "$agent_id" ] || agent_id="main"
mkdir -p "$STATE_DIR" 2>/dev/null || exit 0
f="$STATE_DIR/denied-$(printf '%s-%s' "$session_id" "$agent_id" | tr -c 'A-Za-z0-9_-' '_')"
n="$(cat "$f" 2>/dev/null)"; [[ "$n" =~ ^[0-9]+$ ]] || n=0
[ "$n" -ge "$MAX_DENIALS" ] && exit 0
printf '%s\n' "$((n + 1))" > "$f" 2>/dev/null || exit 0

jq -n --arg m "$trimmed" --arg min "$MIN_CHARS" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "deny",
    permissionDecisionReason: ("SubagentHandback refused: its message is your whole report to your coordinator, which is not shown it anywhere else, and \"" + $m + "\" is not a report. Wait for every other call you made to return, then call SubagentHandback again, alone in its message, with the full report: what you did, what you found, Citations and Assumptions. (Fewer than " + $min + " characters is refused.)")
  }
}' 2>/dev/null
exit 0
