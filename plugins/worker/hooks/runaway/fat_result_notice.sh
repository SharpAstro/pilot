#!/usr/bin/env bash
# PostToolUse hook (wire with matcher "" -- every tool): tells whichever
# agent just made a tool call that the RESULT it just received was unusually
# large, before that agent goes on to reason over it, copy it, or otherwise
# spend more tokens re-processing it.
#
# This hook CANNOT block anything -- PostToolUse fires after the tool already
# ran and the result already exists. It can only hand the calling agent an
# `additionalContext` string, which is the sole lever it has.
#
# DELIBERATE DESIGN NOTE: a PostToolUse hook can only ever speak to the
# calling agent, never to the parent/coordinator that spawned it. So the
# instruction below ("note the overspend in your final report") is not
# decorative -- it is the ONLY route by which this fact can ever reach the
# coordinator: the agent has to choose to carry it into its own
# SubagentHandback/SendMessage. If the agent ignores the notice, the
# coordinator never finds out from this hook. That is a real limitation, not
# an oversight -- see the README.
#
# Threshold: CLAUDE_FAT_RESULT_THRESHOLD_BYTES, default 50000 bytes (~12,500
# tokens at the standard ~4 bytes/token heuristic).
#
# Measured 2026-09-16 against the real corpus: n=66,001 text-bearing
# tool_result payloads, from 1,975 subagent transcripts across 13 projects
# (~/.claude/projects/*/*/subagents/agent-*.jsonl, including nested
# subagents/workflows/wf_*/agent-*.jsonl), spanning 2026-08-15 to
# 2026-09-16. "Payload" is the flattened text of one tool_result content
# block (a plain string, or the joined text of array content blocks); 422
# further tool_result blocks in the same corpus carry no text at all
# (images, tool references) and are excluded from n, since
# tool_response carries no text for those and this hook already fails open
# on them regardless of threshold.
#
# Distribution (bytes): median 392, p90 5,974, p95 11,408, p99 38,038,
# max 86,967, mean 2,580.
#
# Firing rate at each candidate threshold: 10,000 bytes fires on 5.786% of
# n, 30,000 (the prior default) on 1.476%, 50,000 (current) on 0.555%.
# 50,000 sits closest to this hook's own stated design goal, the genuine
# outlier tail (roughly the top 0.5%); 30,000 was firing about 3x more
# often than that.
#
# CAVEAT THAT MAKES THIS NUMBER FRAGILE: 52% of the measured population
# (34,415 of 66,001 payloads) is one project's bulk workload (cad-party, a
# byte-level CAD-format reverse-engineering task) whose payloads run
# systematically smaller than the rest of the corpus. Exclude it and the
# firing rate at every threshold roughly doubles: 30,000 rises to 2.957%,
# 50,000 to 1.146%. A reader whose own work looks nothing like that bulk
# workload should expect this hook to fire closer to 1% of calls than
# 0.5%.
#
# WRITE-SHAPED TOOLS ARE MEASURED ON THEIR DIFF, NOT THEIR FILE. An Edit
# response carries `originalFile`, the WHOLE pre-edit file, and a Write
# response carries `content`, the whole written file. Neither ever enters the
# calling agent's context -- the harness renders a short confirmation and a
# diff -- so measuring the compact JSON of the whole response made a one-line
# edit to a 200 KB file look like a runaway read, and then told the agent to
# "prefer reading specific ranges", advice that means nothing after a write.
# A live campaign reported exactly that. For those tools the echo fields are
# dropped and what remains (the patch) is measured, so a genuinely enormous
# diff still fires. The drop is keyed on the tool name rather than the field
# name on purpose: a read-shaped tool whose response happens to carry
# `.content` is the case this hook exists for, and must keep being measured.
#
# Must be silent in the normal case: this runs on EVERY tool call, so under
# threshold it emits nothing at all -- no stdout, no log write, exit 0
# immediately. Only the over-threshold path does any work.
#
# Safety: set -u (not -e). Fails OPEN (silent, exit 0) on empty/unparseable
# stdin, missing tool_response, or any jq failure.
set -u

THRESHOLD="${CLAUDE_FAT_RESULT_THRESHOLD_BYTES:-50000}"
[[ "$THRESHOLD" =~ ^[0-9]+$ ]] || THRESHOLD=50000

input="$(cat 2>/dev/null)"
[ -z "$input" ] && exit 0

# `tool_response` is a plain string for some tools and a structured object
# for others, with no text field common to all. A string is measured
# directly, which is exactly what the corpus above measured; anything else
# is measured as compact JSON, which over-measures slightly by its
# escaping. That is the safe direction for a warning-only hook: warn early
# rather than miss a genuine overspend. A null tool_response serialises to
# the 4 byte string "null", under any sane threshold, so a missing field
# still fails open silently.
#
# This previously read `.tool_output.output`. That field has never existed
# in any version of the payload, so the threshold comparison below never
# ran even once: jq resolved it to null and the emptiness check exited. The
# measured threshold was always sound; nothing was ever compared against it.
tool_name="$(printf '%s' "$input" | jq -r '.tool_name // "unknown"' 2>/dev/null)"

output_text="$(printf '%s' "$input" | jq -r --arg tool "$tool_name" '
    .tool_response
    | if type == "string" then .
      elif (type == "object" and ($tool == "Edit" or $tool == "Write"
                                  or $tool == "MultiEdit" or $tool == "NotebookEdit"))
        then (del(.originalFile, .oldString, .newString, .content) | @json)
      else (. | @json) end
' 2>/dev/null)"
[ $? -ne 0 ] && exit 0
[ -z "$output_text" ] && exit 0

size="$(printf '%s' "$output_text" | wc -c 2>/dev/null)"
[[ "$size" =~ ^[0-9]+$ ]] || exit 0

# Fast, silent path: this is the common case, executed on every tool call.
[ "$size" -le "$THRESHOLD" ] && exit 0

# --- over threshold: notify and log ---------------------------------------
STATE_DIR="${CLAUDE_FAT_RESULT_STATE_DIR:-${HOME}/.claude/state/fat-result}"
LOG="$STATE_DIR/fat-result-log.tsv"
mkdir -p "$STATE_DIR" 2>/dev/null

session_id="$(printf '%s' "$input" | jq -r '.session_id // "unknown"' 2>/dev/null)"
agent_id="$(printf '%s' "$input" | jq -r '.agent_id // "coordinator"' 2>/dev/null)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null)"
[ -z "$timestamp" ] && timestamp="unknown-time"

approx_tokens=$((size / 4))

printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$timestamp" "$session_id" "$agent_id" "$tool_name" "$size" "$approx_tokens" >> "$LOG" 2>/dev/null

jq -n --arg size "$size" --arg tok "$approx_tokens" --arg tool "$tool_name" '{
  hookSpecificOutput: {
    hookEventName: "PostToolUse",
    additionalContext: ("The result of your last " + $tool + " call was " + $size + " bytes (roughly " + $tok + " tokens) -- unusually large. From here on, prefer reading specific ranges (line ranges, offsets, greps) rather than whole files or unbounded output for the rest of this task. Also note this overspend explicitly in your final report/handback -- this is a PostToolUse hook, it cannot tell your coordinator directly, so you are the only path this information has to reach them.")
  }
}'
exit 0
