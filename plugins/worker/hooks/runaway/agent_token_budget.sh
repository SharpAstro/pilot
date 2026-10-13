#!/usr/bin/env bash
# PreToolUse hook (wire with matcher "" -- every tool): caps a single subagent
# by WEIGHTED TOKEN COST instead of raw call count (see agent_call_budget.sh,
# the per-call sibling this was modelled on).
#
# WEIGHTED COST is a rough PRICE PROXY, not a token count:
#     cost = input + 1.25*cache_creation_input_tokens + 0.1*cache_read_input_tokens + 5*output
# Anthropic bills cache writes at ~1.25x base input, cache reads at ~0.1x base
# input, and output at ~5x base input. Summing raw usage fields overstates true
# spend by roughly 5x on a cache-heavy transcript, because cache_read tokens
# dominate the raw count but cost a tenth as much as fresh input. DO NOT "fix"
# this into a plain sum of the four fields -- that reintroduces the exact
# distortion this hook exists to remove. If the per-token prices above change,
# update the four constants below and this comment together.
#
# Only calls carrying an `agent_id` are gated (i.e. calls made by a subagent);
# the coordinator's own tool calls are never counted or gated here, same
# convention as agent_call_budget.sh.
#
# TRANSCRIPT LOCATION: a subagent's own transcript is not named in the
# PreToolUse payload. It lives on disk as one JSONL per agent, named
# <agent_id>.output, inside a `tasks` directory that is a SIBLING of the
# payload's `scratchpad_dir` (both live directly under the session's tmp
# directory). That file is itself a symlink to
# ~/.claude/projects/<project-slug>/subagents/agent-<agent_id>.jsonl -- we
# never need to know the project slug ourselves, the tasks-dir symlink does
# that translation for us. Verified against a live session on 2026-09-16 (see
# the accompanying report's section 1 for the ls/readlink evidence).
#
# BUDGET RESOLUTION ORDER, latest marker in transcript order wins:
#   1. A `[[TOKEN_BUDGET=<integer>]]` marker found in the FIRST user-role
#      entry of the agent's OWN transcript -- the brief the parent wrote when
#      spawning it, and ONLY that entry (mirrors agent_call_budget.sh's
#      CALL_BUDGET brief convention and its self-raise guarantee: nothing the
#      agent itself says later can be this entry). Read once per agent and
#      cached -- found-or-not-found -- never rescanned.
#   2. A COORDINATOR GRANT appearing later in the same transcript -- the
#      parent raising (or lowering) the ceiling after the agent has already
#      started. A grant is an absolute ceiling, never an increment, and it
#      REPLACES whatever the brief marker said. It can arrive in either of
#      two shapes, because a coordinator message reaches a subagent's
#      transcript differently depending on whether the agent is still
#      running or was resumed after handing back:
#        (a) ATTACHMENT shape (agent still running when the coordinator
#            wrote): `.type=="attachment"`, `.attachment.isMeta==true`,
#            `.attachment.origin=={"kind":"coordinator"}`, with the text in
#            `.attachment.prompt`.
#        (b) RESUMED-USER shape (agent had already called SubagentHandback
#            and was resumed): `.type=="user"`, a TOP-LEVEL
#            `.origin=={"kind":"coordinator"}`, `.isMeta==true`, and
#            `.message.content` as a plain STRING (no toolUseResult, no
#            sourceToolAssistantUUID -- those belong to tool-result entries,
#            never to this shape).
#      If more than one grant is found, the one LATEST in transcript order
#      wins, whichever shape each one is in.
#   3. $CLAUDE_AGENT_TOKEN_BUDGET, when it is a plain non-negative integer
#      and no marker (brief or grant) was found.
#   4. Built-in default (see DEFAULT_TOKEN_BUDGET below), in WEIGHTED units,
#      when nothing above resolved.
#
# WHY ONLY THOSE TWO SHAPES, AND NOTHING ELSE, COUNTS AS A GRANT: an agent
# cannot author either shape itself. Its own output only ever lands in this
# transcript as `assistant`-role entries or (structurally, from the harness)
# as `user`-role tool-result entries carrying `toolUseResult` -- and a
# tool-result entry never carries a top-level `origin` key (verified against
# 27 real tool-result entries in a live transcript: zero carried one). So
# `.type=="user" and .origin.kind=="coordinator"` cannot be forged by
# anything the agent does mid-run, including a tool result whose CONTENT
# happens to contain marker-shaped text (a file Read back, a command's
# stdout, etc.) -- that text sits inside `.message.content`, not behind a
# top-level `.origin`, so it is structurally excluded, not just filtered by
# convention.
#
# MARKER SYNTAX: [[TOKEN_BUDGET=<digits>]] -- e.g. [[TOKEN_BUDGET=250000]].
# Matches agent_call_budget.sh's bracketed CALL_BUDGET convention exactly,
# and for the same reason: double square brackets plus an `=<digits>` glued
# on with no whitespace is not how prose casually states a number, so a
# document that merely DISCUSSES this hook doesn't accidentally set a real
# budget. There is no other accepted spelling.
#
# PERFORMANCE -- CHEAP PATH vs CONDITIONAL EXPENSIVE SCAN: this fires on
# every tool call a subagent makes, so nothing here may re-read the whole
# transcript on every call as a matter of course.
#   - The WEIGHTED TOTAL is always kept incremental: a running total and the
#     byte offset already consumed are cached per agent, and each call reads
#     only the bytes appended since that offset (`tail -c +OFFSET`, which
#     seeks rather than streams on a regular file). A partial trailing line
#     (the transcript can be read mid-write) is held back and re-read next
#     call rather than fed to jq.
#   - The BRIEF marker is read once (the first user-role entry only) and
#     cached found-or-not-found, same as before.
#   - The GRANT scan -- walking the whole transcript for the coordinator
#     shapes above -- is the one genuinely expensive operation here, and it
#     is NOT cached, because a new grant can land between any two calls. It
#     is therefore made CONDITIONAL instead: it only runs when the cheap
#     total-vs-budget comparison is already AT OR OVER the cheap budget
#     (brief marker, else env var, else default). Under that threshold, no
#     grant could possibly change an ALLOW into anything else, so the scan
#     is skipped outright. Only once the agent is at risk of being denied
#     does the cost of checking "did the coordinator raise this?" become
#     worth paying.
#
# Over budget: deny, telling the agent it has hit a CHECKPOINT, not a
# failure -- stop calling any tool other than SubagentHandback or
# SendMessage, report where it got to, what remains, and what it estimates
# it still needs, and leave the work resumable, since the coordinator may
# send it back with a larger budget. Because a resumed agent's grant is now
# read from the RESUMED-USER shape above, SubagentHandback is a safe, load-
# bearing way for it to report: the loop that used to happen when a denial
# told an agent to report back while also denying the tools it reports back
# WITH is exactly the non-negotiable lesson recorded in ~/.claude/CLAUDE.md,
# and both SubagentHandback and SendMessage stay permitted at every budget
# state on every path here, including every error path, for that reason.#
# Neither of those two tools is guaranteed to BE in a given subagent's
# toolset, though. A live campaign found three Sonnet assistants that had
# neither: each reported both as unavailable and then went silent, and none
# of them wrote the evidence file it was carrying. So the denial text names
# a third route that depends on no tool at all -- stop calling tools and
# write the report as the final text message, which is what reaches the
# coordinator anyway.
#
# Safety: set -u (not -e). Fails OPEN on empty/unparseable stdin, no
# agent_id, a missing/unreadable transcript, malformed JSON, any jq failure
# (including inside the grant scan -- a jq failure there just means "no
# grant found", falling back to the cheap budget, never a denial caused by
# the failure itself), or lock contention. Never hangs: lock wait capped at
# ~200ms total, same as agent_call_budget.sh.
#
# --- 2026-09-17: grant-shape + conditional-scan fix -----------------------
# Three defects fixed in the same change, against a drafted patch that was
# reviewed before landing:
#   1. The draft's grant path read only the ATTACHMENT shape (a). A
#      checkpointed agent is told to call SubagentHandback, which ENDS it,
#      so any grant sent to resume it afterwards can only ever arrive in the
#      RESUMED-USER shape (b) -- the draft was structurally blind to every
#      grant sent to an agent that had already handed back. Both shapes are
#      now read, and whichever is LATEST in transcript order wins.
#   2. The draft lowered DEFAULT_TOKEN_BUDGET to 500,000. Rejected: the
#      2,000,000 below is the measured number and stays.
#   3. The draft re-ran the full grant scan on EVERY tool call, which is
#      brutal on a long-running agent making hundreds of calls. The scan is
#      now conditional on the cheap total already being at or over the cheap
#      budget (see PERFORMANCE above) -- it runs only at the moment it could
#      actually change the answer.
# Also folded in here: the marker syntax moved from the loose
# `TOKEN_BUDGET: <digits>` form to the bracketed `[[TOKEN_BUDGET=<digits>]]`
# form, matching agent_call_budget.sh, and the brief scan was narrowed to
# ONLY the first user-role entry (it previously scanned every user-role
# entry in the transcript for the loose form, which is also how a tool
# result's own content could have been mistaken for a declaration). The
# loose form is a live bug on this machine: a brief that merely quoted
# `TOKEN_BUDGET: 99999999` as an example of what NOT to write set that as a
# real budget. The bracketed form is not matched by that sentence.
#
# --- 2026-09-17 (same day, later): hard maximum -----------------------
# Everything above makes a marker or a grant an absolute ceiling -- but with
# no upper bound, that ceiling was voluntary: any brief that merely quotes a
# marker, or any coordinator, could set an arbitrarily large number and the
# hook would obey it. MAX_TOKEN_BUDGET below is the backstop: the FINAL
# resolved budget, from every source (brief marker, grant, env var, or the
# default), is clamped to it via the single clamp_budget function -- one
# clamp, called at every point a candidate becomes "the budget", so nothing
# bypasses it. A clamp is visible in three places: the log line's decision
# field gets a "-CLAMPED" suffix, the cache stores the CLAMPED value (never
# the raw request, so the two can't disagree on a later call), and a denial
# caused by a clamped ceiling names BOTH the requested and the enforced
# figure. CLAUDE_AGENT_TOKEN_BUDGET_MAX may lower this constant further, per
# call, but can never raise it -- doing that requires editing this file.
set -u

# --- state location -----------------------------------------------------
# Mirrors agent_call_budget.sh's convention (a directory under ~/.claude/state)
# for when this is actually installed. Overridable so this script can be
# exercised in a test harness without writing anywhere near ~/.claude.
STATE_DIR="${CLAUDE_TOKEN_BUDGET_STATE_DIR:-${HOME}/.claude/state/token-budget}"
LOG="$STATE_DIR/token-budget-log.tsv"
mkdir -p "$STATE_DIR" 2>/dev/null

# Weighted-cost coefficients (see header comment).
W_CACHE_CREATION="1.25"
W_CACHE_READ="0.1"
W_OUTPUT="5"

# Default budget in WEIGHTED units. Measured 2026-09-16 from the real
# corpus: n=1,967 subagent transcripts across 13 projects
# (~/.claude/projects/*/*/subagents/agent-*.jsonl, including nested
# subagents/workflows/wf_*/agent-*.jsonl), 2026-08-15 to 2026-09-16,
# weighted by the formula above: median 81,235, p75 512,030, p90 1,727,302,
# p95 3,263,059, p99 10,758,978, max 107,449,485.
#
# A re-scan of the same glob on 2026-09-16 found 1,975 files, not 1,967 --
# 8 more transcripts than the measurement above used. The percentiles below
# were not rerun against the larger count; they are reported as measured,
# against the population and date stated above, so a future reader can tell
# they may be very slightly stale rather than assuming they are current.
#
# 10,000,000 sits just under the measured p99 (10,758,978), cutting off
# roughly 1.17% of agents (23 of 1,967) -- catch the worst one percent,
# leave everything else alone. The earlier justification on this line was
# wrong on two counts: it argued for a DIFFERENT number, 2,000,000, not the
# 10,000,000 actually set below, and it was derived from an unverifiable
# 102-transcript sample that could not be reproduced from the real
# 1,967-transcript population. Checked against the real data, 2,000,000
# falls between the real p90 (1,727,302) and p95 (3,263,059), not where the
# old comment placed it, and was rejected anyway: it cuts off 8.24% of the
# population (162 agents), rising to 16.96% once the single anomalous bulk
# day (2026-09-01, 51.5% of all 1,967 transcripts) is excluded (n=955) --
# against 10,000,000, which only moves from 1.17% to 2.41% under the same
# exclusion. The strongest argument for a cap at all is the maximum,
# 107,449,485 -- more than ten times the cap, and roughly 1,320 times the
# median. A single agent spent that; that is the runaway this hook exists
# to stop, and it is not hypothetical.
DEFAULT_TOKEN_BUDGET=2000000

# Hard maximum, in WEIGHTED units (2026-09-17). Until now a marker or a
# coordinator grant was an absolute ceiling with NO upper bound, which made
# the whole control voluntary -- any brief that merely quotes a marker as an
# example, or any coordinator, could set an arbitrarily large number and the
# hook would obey it. This is the backstop no marker, no grant and no env
# var can exceed. From the same 2026-09-16 corpus measurement as
# DEFAULT_TOKEN_BUDGET above: p90 3.0M, p99 17.6M -- 10,000,000 sits between
# them, killing roughly the top 3 to 5 percent of agents outright regardless
# of what any source claims their budget should be.
MAX_TOKEN_BUDGET=10000000

# The compiled maximum above may only be LOWERED by this env var, never
# raised -- read it, validate it, and use it only when it is strictly less
# than MAX_TOKEN_BUDGET. A value that is invalid or >= the compiled constant
# is silently ignored and the constant stands. Raising the real ceiling
# requires editing this file, a deliberate act by a human.
effective_max_token_budget="$MAX_TOKEN_BUDGET"
if [ -n "${CLAUDE_AGENT_TOKEN_BUDGET_MAX:-}" ] && [[ "${CLAUDE_AGENT_TOKEN_BUDGET_MAX}" =~ ^[0-9]+$ ]] && [ "${CLAUDE_AGENT_TOKEN_BUDGET_MAX}" -lt "$MAX_TOKEN_BUDGET" ]; then
    effective_max_token_budget="$CLAUDE_AGENT_TOKEN_BUDGET_MAX"
fi

# clamp_budget VALUE -- the ONE clamp, called at EVERY point a candidate
# budget (brief marker, grant, env var, or the default) is finalized, so no
# source can bypass it. Sets CLAMP_RESULT to min(VALUE, effective max) and
# CLAMP_REQUESTED to VALUE when VALUE actually exceeded the max (empty
# otherwise) -- the caller must read both immediately, since the next call
# to this function overwrites them. Also sets the sticky any_clamped=1 flag
# (never reset back to 0 within one invocation) so the log line for this
# call can record that clamping happened somewhere in resolving it, even if
# a later, unclamped grant went on to supersede the clamped value.
any_clamped=0
clamp_budget() {
    local v="$1"
    CLAMP_REQUESTED=""
    if [ "$v" -gt "$effective_max_token_budget" ]; then
        CLAMP_RESULT="$effective_max_token_budget"
        CLAMP_REQUESTED="$v"
        any_clamped=1
    else
        CLAMP_RESULT="$v"
    fi
}

input="$(cat 2>/dev/null)"
[ -z "$input" ] && exit 0

agent_id="$(printf '%s' "$input" | jq -r '.agent_id // empty' 2>/dev/null)"
[ $? -ne 0 ] && exit 0
[ -z "$agent_id" ] && exit 0   # coordinator's own call: never gated here

session_id="$(printf '%s' "$input" | jq -r '.session_id // empty' 2>/dev/null)"
[ $? -ne 0 ] && exit 0
[ -z "$session_id" ] && session_id="unknown"

tool_name="$(printf '%s' "$input" | jq -r '.tool_name // empty' 2>/dev/null)"
[ $? -ne 0 ] && exit 0

scratchpad_dir="$(printf '%s' "$input" | jq -r '.scratchpad_dir // empty' 2>/dev/null)"
[ $? -ne 0 ] && scratchpad_dir=""

# transcript_path is the second route to the agent's own transcript, used
# when the tasks symlink is not under the session dir scratchpad_dir names.
# Neither field alone is enough, so neither one being absent is fatal here;
# only failing to resolve a transcript from BOTH is (see below).
transcript_path="$(printf '%s' "$input" | jq -r '.transcript_path // empty' 2>/dev/null)"
[ $? -ne 0 ] && transcript_path=""
[ -z "$scratchpad_dir" ] && [ -z "$transcript_path" ] && exit 0   # fail open

timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null)"
[ -z "$timestamp" ] && timestamp="unknown-time"

safe_agent_id="$(printf '%s' "$agent_id" | tr -c 'A-Za-z0-9_-' '_')"

log_line() {
    # ts, session, agent, tool, weighted_total, budget, decision
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$timestamp" "$session_id" "$safe_agent_id" "$tool_name" "$1" "$2" "$3" >> "$LOG" 2>/dev/null
}

# find_latest_grant TRANSCRIPT -- prints the digits of the LATEST valid
# [[TOKEN_BUDGET=N]] marker found in a coordinator grant (either shape, see
# header), or prints nothing if none is found. Never fails the caller: any
# jq error or malformed line just yields no output, i.e. no grant, i.e. the
# cheap budget stands -- fail-open by construction.
find_latest_grant() {
    local xscript="$1"
    local raw line candidate marker
    marker=""
    raw="$(jq -c '
        select(
          (.type=="attachment" and (.attachment.isMeta==true) and (.attachment.origin.kind=="coordinator"))
          or
          (.type=="user" and (.isMeta==true) and (.origin.kind=="coordinator") and ((.message.content|type)=="string"))
        )
        | (if .type=="attachment" then .attachment.prompt else .message.content end)
    ' "$xscript" 2>/dev/null)"
    [ -z "$raw" ] && { printf ''; return; }
    while IFS= read -r line; do
        [ -z "$line" ] && continue
        candidate="$(printf '%s' "$line" | jq -r '
            [scan("\\[\\[TOKEN_BUDGET=([0-9]+)\\]\\]")] | if length>0 then .[0][0] else "" end
        ' 2>/dev/null)"
        if [ -n "$candidate" ] && [[ "$candidate" =~ ^[0-9]+$ ]]; then
            marker="$candidate"
        fi
    done <<< "$raw"
    printf '%s' "$marker"
}

# --- locate this agent's transcript -------------------------------------
# resolve_transcript SCRATCHPAD_DIR TRANSCRIPT_PATH AGENT_ID -- prints the
# path to this subagent's OWN transcript, or nothing if it cannot be found.
#
# The harness writes a symlink `<session tmp dir>/tasks/<agent_id>.output`
# pointing at `<config>/projects/<slug>/<session>/subagents/agent-<id>.jsonl`.
# It does NOT reliably write that symlink under the session dir that
# `scratchpad_dir` names. Observed 2026-09-21: sessions e220ef5d and
# c0ddcedc had every one of their subagent symlinks written into a THIRD
# session's tasks dir under the same project slug (0a3c35f7), and neither
# had a `tasks/` dir of its own at all. The old single-path lookup therefore
# resolved nothing for 1,364 consecutive calls across those two sessions:
# every subagent fell to DEFAULT_BUDGET with its `[[CALL_BUDGET=n]]` marker
# unread, and every coordinator grant was invisible too, because the grant
# scan reads the same file. The sibling token hook logged
# ALLOW-NOTRANSCRIPT on 100% of the same calls, which is how the two halves
# of the symptom were tied to one cause.
#
# So this tries, in order, and takes the first readable hit:
#   1. the documented location under this session's own tmp dir;
#   2. the payload's `transcript_path` itself, when it already names this
#      agent's own jsonl rather than the coordinator's session transcript;
#   3. the symlink's TARGET, derived from `transcript_path` -- the session
#      transcript is `<dir>/<session>.jsonl` and the session's `subagents/`
#      dir sits beside it at `<dir>/<session>/subagents/`. Needs no symlink;
#   4. a sibling session's tasks dir under the same project -- the shape
#      actually observed above. Agent ids are unique, so a hit here is this
#      agent's transcript wherever the harness happened to file it.
resolve_transcript() {
    local sp="$1" tp="$2" aid="$3"
    local sdir cand

    if [ -n "$sp" ]; then
        sdir="$(dirname "$sp" 2>/dev/null)"
        cand="$sdir/tasks/${aid}.output"
        if [ -r "$cand" ]; then printf '%s' "$cand"; return 0; fi
    fi

    if [ -n "$tp" ]; then
        case "$tp" in
            */agent-"$aid".jsonl)
                if [ -r "$tp" ]; then printf '%s' "$tp"; return 0; fi
                ;;
        esac
        cand="${tp%.jsonl}/subagents/agent-${aid}.jsonl"
        if [ -r "$cand" ]; then printf '%s' "$cand"; return 0; fi
    fi

    if [ -n "$sp" ]; then
        sdir="$(dirname "$(dirname "$sp" 2>/dev/null)" 2>/dev/null)"
        if [ -n "$sdir" ] && [ "$sdir" != "/" ] && [ "$sdir" != "." ]; then
            for cand in "$sdir"/*/tasks/"${aid}.output"; do
                if [ -r "$cand" ]; then printf '%s' "$cand"; return 0; fi
            done
        fi
    fi

    return 1
}

transcript="$(resolve_transcript "$scratchpad_dir" "$transcript_path" "$agent_id" 2>/dev/null)"
if [ -z "${transcript:-}" ] || [ ! -r "$transcript" ]; then
    log_line "0" "?" "ALLOW-NOTRANSCRIPT"
    exit 0
fi

# --- per-agent cache files -----------------------------------------------
offset_file="$STATE_DIR/offset-${session_id}-${safe_agent_id}"
total_file="$STATE_DIR/weighted-${session_id}-${safe_agent_id}"
budget_file="$STATE_DIR/budget-${session_id}-${safe_agent_id}"
lock_dir="$STATE_DIR/.lock-${session_id}-${safe_agent_id}"

locked=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
    if mkdir "$lock_dir" 2>/dev/null; then
        locked=1
        break
    fi
    sleep 0.02
done

# --- resolve the CHEAP budget (once, cached) ------------------------------
# Brief marker if cached, else the first-entry scan (once), else the env
# var, else the default. No transcript scan for grants happens here. The
# cache file holds "<clamped budget>\t<raw requested, or empty>" so a later
# call never has to re-derive whether the cached figure was clamped -- the
# CLAMPED value is what is cached, per the 2026-09-17 clamp fix, so the
# cache and the enforced ceiling can never disagree.
budget=""
budget_requested=""
if [ -f "$budget_file" ]; then
    raw="$(cat "$budget_file" 2>/dev/null)"
    if [ "$raw" = "NONE" ]; then
        budget="NONE"
    else
        cached_budget="${raw%%$'\t'*}"
        cached_requested="${raw#*$'\t'}"
        # cached_requested unchanged from raw means there was no tab at all
        # -- a malformed/pre-clamp-era cache write. Treat as a miss.
        if [[ "$cached_budget" =~ ^[0-9]+$ ]] && [ "$cached_requested" != "$raw" ]; then
            budget="$cached_budget"
            [[ "$cached_requested" =~ ^[0-9]+$ ]] && budget_requested="$cached_requested"
        fi
    fi
fi

if [ -z "$budget" ]; then
    # First time we've seen this agent: look ONLY at the FIRST user-role
    # entry in its own transcript -- the brief the parent wrote before the
    # agent's first tool call -- for a [[TOKEN_BUDGET=<digits>]] marker.
    # Restricting to that one entry (rather than every user-role entry in
    # the transcript) is the same self-raise guarantee agent_call_budget.sh
    # rests on: see the header's WHY ONLY THOSE TWO SHAPES section for why a
    # later user-role entry (a tool result, say) cannot be mistaken for this.
    first_user_text="$(jq -c 'select(.type=="user")' "$transcript" 2>/dev/null | head -1 | jq -r '
        .message.content
        | if type=="string" then .
          elif type=="array" then ([.[]? | select(.type=="text") | .text] | join("\n"))
          else "" end
    ' 2>/dev/null)"
    found=""
    if [ -n "$first_user_text" ]; then
        found="$(printf '%s' "$first_user_text" | grep -oE '\[\[TOKEN_BUDGET=[0-9]+\]\]' | head -1 | grep -oE '[0-9]+')"
    fi
    if [[ "$found" =~ ^[0-9]+$ ]]; then
        clamp_budget "$found"
        budget="$CLAMP_RESULT"
        budget_requested="$CLAMP_REQUESTED"
        printf '%s\t%s\n' "$budget" "$budget_requested" > "$budget_file" 2>/dev/null
    else
        budget="NONE"
        printf 'NONE\n' > "$budget_file" 2>/dev/null
    fi
fi

if [ "$budget" = "NONE" ]; then
    if [ -n "${CLAUDE_AGENT_TOKEN_BUDGET:-}" ] && [[ "${CLAUDE_AGENT_TOKEN_BUDGET}" =~ ^[0-9]+$ ]]; then
        clamp_budget "$CLAUDE_AGENT_TOKEN_BUDGET"
    else
        clamp_budget "$DEFAULT_TOKEN_BUDGET"
    fi
    budget="$CLAMP_RESULT"
    budget_requested="$CLAMP_REQUESTED"
fi

# --- incremental weighted-total update ------------------------------------
offset=0
if [ -f "$offset_file" ]; then
    raw="$(cat "$offset_file" 2>/dev/null)"
    [[ "$raw" =~ ^[0-9]+$ ]] && offset="$raw"
fi

total=0
if [ -f "$total_file" ]; then
    raw="$(cat "$total_file" 2>/dev/null)"
    [[ "$raw" =~ ^[0-9]+$ ]] && total="$raw"
fi

size="$(stat -c%s "$transcript" 2>/dev/null)"
if [[ "$size" =~ ^[0-9]+$ ]] && [ "$size" -gt "$offset" ]; then
    chunk="$(tail -c +"$((offset + 1))" "$transcript" 2>/dev/null)"
    if [ -n "$chunk" ]; then
        # NOTE: bash command substitution unconditionally strips trailing
        # newlines from $chunk, so `$chunk` itself can never be checked for
        # a trailing newline directly -- that check is always false. Ask the
        # FILE instead: `tail -c1` on the file, captured the same
        # newline-stripping way, comes back EMPTY iff the file's true last
        # byte (at the moment we stat'd it) was a newline.
        last_byte="$(tail -c1 "$transcript" 2>/dev/null)"
        new_offset="$offset"
        if [ -z "$last_byte" ]; then
            # File ends on a newline: every byte we read is a complete line.
            complete_chunk="$chunk"
            new_offset="$size"
        else
            # File ends mid-line (still being written): hold back the
            # trailing partial line and re-read it next call.
            complete_chunk="${chunk%$'\n'*}"
            if [ -n "$complete_chunk" ]; then
                complete_chunk="${complete_chunk}"$'\n'
                consumed="$(printf '%s' "$complete_chunk" | wc -c 2>/dev/null)"
                [[ "$consumed" =~ ^[0-9]+$ ]] && new_offset=$((offset + consumed))
            fi
        fi
        if [ -n "$complete_chunk" ]; then
            delta="$(printf '%s' "$complete_chunk" | jq -s \
                --argjson wc "$W_CACHE_CREATION" --argjson wr "$W_CACHE_READ" --argjson wo "$W_OUTPUT" '
                [ .[]? | select(.type=="assistant") | (.message.usage // {}) |
                  ((.input_tokens // 0)
                   + $wc*(.cache_creation_input_tokens // 0)
                   + $wr*(.cache_read_input_tokens // 0)
                   + $wo*(.output_tokens // 0))
                ] | (add // 0) | floor
            ' 2>/dev/null)"
            if [[ "$delta" =~ ^[0-9]+$ ]]; then
                total=$((total + delta))
                offset="$new_offset"
                printf '%s\n' "$total" > "$total_file" 2>/dev/null
                printf '%s\n' "$offset" > "$offset_file" 2>/dev/null
            fi
            # jq failure (malformed/partial line slipped through): leave
            # offset/total untouched, fail open, try again next call.
        fi
    fi
fi

[ "$locked" -eq 1 ] && rmdir "$lock_dir" 2>/dev/null

# --- conditional grant scan (2026-09-17 fix, defect 3) --------------------
# The expensive whole-transcript grant scan only runs once the cheap total
# is already at or over the cheap budget -- under that, no grant could
# change an ALLOW, so it is skipped outright (see PERFORMANCE in the
# header). effective_budget starts as the cheap budget and is only replaced
# if a valid grant is actually found. A found grant is clamped exactly like
# every other source (see clamp_budget above) and is NOT cached -- the token
# hook's grant scan stays uncached by design (see GRANTS in the header).
effective_budget="$budget"
if [ "$total" -ge "$budget" ]; then
    grant_val="$(find_latest_grant "$transcript")"
    if [[ "$grant_val" =~ ^[0-9]+$ ]]; then
        clamp_budget "$grant_val"
        effective_budget="$CLAMP_RESULT"
        budget_requested="$CLAMP_REQUESTED"
    fi
fi

clamp_suffix=""
[ "$any_clamped" -eq 1 ] && clamp_suffix="-CLAMPED"

if [ "$total" -gt "$effective_budget" ]; then
    case "$tool_name" in
        SubagentHandback|SendMessage)
            log_line "$total" "$effective_budget" "ALLOW-REPORT${clamp_suffix}"
            exit 0
            ;;
    esac
    log_line "$total" "$effective_budget" "BLOCK${clamp_suffix}"
    jq -n --arg total "$total" --arg budget "$effective_budget" --arg requested "$budget_requested" '
      (if ($requested != "") then
         (" A budget of " + $requested + " weighted units was requested, but this hook enforces a hard maximum, so the ceiling in force here is " + $budget + ", not " + $requested + ".")
       else "" end) as $clampclause
      | ((($total | tonumber) * 2) | floor | tostring) as $suggest
      | ("You have hit a token-budget CHECKPOINT, not a failure (" + $total + " of " + $budget + " weighted units, a price-proxy cost figure, not a raw token count)." + $clampclause + " This is a pause in the work, not the end of it. Every tool is now denied to you EXCEPT SubagentHandback and SendMessage -- stop calling anything else right now. Call SubagentHandback once and report honestly: where you got to, what remains, and what you estimate you still need to finish. If SubagentHandback is not in your toolset, which is what happens whenever this session is not in auto mode, report with SendMessage instead and do not retry SubagentHandback. If NEITHER of those two tools is in your toolset, stop calling tools altogether and write your report as your final text message -- that text is what reaches your coordinator, it needs no tool at all, and it is the one route that always exists. Going quiet is the worst outcome available to you here, because all your coordinator sees is an agent that ended without delivering anything. Name any file you were told to write and did not get to, because your coordinator can have it written, but only if it knows it is missing. Leave your work resumable -- do not treat this as the final report unless it genuinely is one. Your coordinator can read this and send you back with a larger budget, and that grant will be read from your transcript on your next call, so either tool is a safe way to report here. IMPORTANT, so that you ask for a number that actually helps: a grant is an ABSOLUTE ceiling on your CUMULATIVE weighted total, not a fresh allowance. Your total of " + $total + " is never reset by a grant, so a grant must be LARGER than " + $total + " to buy you any room at all, and a grant of N leaves you only N minus " + $total + " further units. Tell your coordinator the TOTAL ceiling you need, never an increment: if you estimate the remaining work costs roughly what you have spent so far, ask for about " + $suggest + ", not " + $total + ".") as $reason
      | {
          hookSpecificOutput: {
            hookEventName: "PreToolUse",
            permissionDecision: "deny",
            permissionDecisionReason: $reason
          }
        }
    '
    exit 0
fi

log_line "$total" "$effective_budget" "ALLOW${clamp_suffix}"
exit 0
