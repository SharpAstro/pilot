#!/usr/bin/env bash
# PreToolUse hook (wired with matcher "" -- every tool): caps the number of
# TOOL CALLS a single subagent may make.
#
# BUDGET RESOLUTION ORDER (precedence, highest first; a later coordinator
# grant always overrides an earlier one -- see step 1b):
#   1a. A brief marker -- [[CALL_BUDGET=<integer>]] -- found in the FIRST
#      user-role message of the subagent's OWN transcript (i.e. the brief it
#      was spawned with, and ONLY that message: see SECURITY below).
#   1b. A COORDINATOR GRANT appearing later in the transcript -- the parent
#      raising (or lowering) the ceiling after the agent already started, or
#      after it checkpointed and was resumed. An absolute ceiling, never an
#      increment: it REPLACES whatever the brief marker (or an earlier
#      grant) said, it does NOT reset the call counter, and it is read from
#      either of two shapes depending on whether the agent was still running
#      or had already handed back when the coordinator wrote it -- see
#      GRANTS below for the shapes and why the scan for them is conditional.
#   2. $CLAUDE_AGENT_CALL_BUDGET, when it is a plain non-negative integer
#      and no marker (brief or grant) was found.
#   3. Built-in default: 80, just under the measured p90 of 86. 80 was
#      chosen over 86 because a percentile off this corpus is not sharp
#      enough to justify the false precision, not because 80 is p90.
#      This was 120, justified by a sample of 24 subagents and the claim
#      that no real agent had ever hit it. Both are superseded. Re-measured
#      2026-09-17 by counting tool_use blocks across 2,085 real subagent
#      transcripts, 0 unparseable: median 8, mean 33.2, p90 86, p95 118,
#      p99 285, max 2,008. The hook fires at 120 on 4.94% of them (103
#      agents), so it was never inert. MAX_CALL_BUDGET of 500 sits above
#      p99 and would have caught 0.48%.
#      At 80, 11.41% of agents checkpoint, against 4.94% at 120. That is
#      affordable only because of the change below: hitting
#      the budget used to end an agent, so a tight default was dangerous,
#      and it now costs a coordinator round trip instead of a dead agent.
#      Lowering it takes effect instantly for every running agent, so it was
#      done deliberately by a human decision (Sam, 2026-09-17), not as a
#      side effect of adding the marker or the grant path below.
#
# WHY A BRIEF MARKER, AND WHY IT LOWERS AS OFTEN AS IT RAISES:
# One global number can't serve both a 10-call read-only lookup and a
# 300-call build-fix-retest loop. Given the measured distribution above,
# the marker's primary job is to LOWER the ceiling for a small brief --
# an agent that should finish in 20 calls and is instead grinding through
# 60 is exactly the runaway this hook exists to catch, and even the
# lowered default of 80 sails past it. Raising the ceiling for a genuinely large
# brief (build + test + fix + retest) is the secondary case; both are the
# same mechanism, a number the brief states up front.
#
# SECURITY -- the property this whole feature rests on: an agent must not
# be able to raise its own ceiling mid-run by emitting the marker text in
# its own output. The BRIEF marker is read ONLY from the FIRST user-role
# JSONL entry in the agent's own transcript -- the brief text the parent
# wrote BEFORE the agent's first tool call -- and that scan stops there.
# The GRANT scan (added 2026-09-17, see GRANTS below) reads further into the
# transcript, but is restricted the same way in spirit: it only accepts the
# two shapes a genuine coordinator message can take, and an agent cannot
# author either one. Everything the agent itself produces lands as
# `assistant`-role entries, or (structurally, from the harness) as
# `user`-role tool-result entries carrying `toolUseResult` -- and a
# tool-result entry never carries a top-level `origin` key (verified against
# 27 real tool-result entries in a live transcript: zero carried one). So
# neither scan can be tripped by anything the agent said or did after it
# started running, including a tool result whose CONTENT happens to contain
# marker-shaped text.
#
# MARKER SYNTAX: [[CALL_BUDGET=<digits>]] -- e.g. [[CALL_BUDGET=250]].
# Chosen to resist being tripped by ordinary prose that happens to discuss
# budgets: double square brackets are rare outside a deliberate directive,
# CALL_BUDGET is a specific uncommon token (not "budget" or "calls", which
# turn up constantly in prose about this very hook), and `=<digits>` glued
# on with no whitespace is not how a sentence would casually state a number.
# Deliberately stricter than the sibling agent_token_budget.sh's OLD
# loosely-matched "TOKEN_BUDGET: <int>" form (a stray quoted example
# containing that exact text once set a real budget of 99999999 there) --
# that sibling has since moved to this same bracketed convention for its own
# marker, so both hooks now share one syntax.
#
# GRANTS -- checkpoint, not kill switch (added 2026-09-17): before this
# date, the brief marker was read once and cached forever, so an agent that
# hit its ceiling was simply dead with no way back -- a kill switch wearing
# a "checkpoint" denial message. A coordinator grant fixes that: it can
# arrive in either of two shapes, because a coordinator message reaches a
# subagent's transcript differently depending on whether the agent was
# still running or had already handed back:
#   (a) ATTACHMENT shape (agent still running when the coordinator wrote):
#       `.type=="attachment"`, `.attachment.isMeta==true`,
#       `.attachment.origin=={"kind":"coordinator"}`, text in
#       `.attachment.prompt`.
#   (b) RESUMED-USER shape (agent had already called SubagentHandback and
#       was resumed): `.type=="user"`, a TOP-LEVEL
#       `.origin=={"kind":"coordinator"}`, `.isMeta==true`, and
#       `.message.content` as a plain STRING (no toolUseResult, no
#       sourceToolAssistantUUID -- those mark a tool-result entry, never
#       this shape).
# If more than one grant is found, the one LATEST in transcript order wins,
# whichever shape each is in. A grant only ever moves the CEILING -- the
# call counter is cumulative and is never reset by one, so an agent at 130
# calls granted a budget of 200 continues counting from 130, not from 0.
#
# CONDITIONAL SCAN, AND WHY THIS HOOK CACHES A HONOURED GRANT (the token
# hook deliberately does NOT): walking the whole transcript for a grant on
# every single tool call would be brutal on a long-running agent, so it only
# happens once the incremented call count has actually reached or passed
# the currently cached budget -- under that, no grant could change an
# ALLOW, so the scan is skipped outright. When a scan DOES run and finds a
# valid grant, that value is written back into this agent's cached budget
# file, so the next call resolves the new ceiling straight from cache
# without rescanning -- and will only trigger another scan once the call
# count reaches or passes THAT ceiling in turn. This is safe to cache,
# unlike the token hook's weighted total: this hook's trigger is a plain
# monotonic call count against a value the cache itself controls, so a scan
# is only ever skipped when it provably could not have changed the answer,
# and "latest wins" still holds globally because every scan that DOES run
# re-reads the FULL transcript rather than resuming from where the last one
# left off.
#
# TRANSCRIPT LOCATION: a subagent's own transcript is not reliably named
# directly in the PreToolUse payload, so this derives it -- the same way the
# sibling agent_token_budget.sh does -- from `scratchpad_dir`, falling back
# to `transcript_path` and then to a sibling-session scan. See
# resolve_transcript() below for the four candidates and for the 2026-09-21
# incident that made the fallbacks necessary: the symlink is NOT always
# written under the session dir `scratchpad_dir` names, and when it is
# missing this hook is silently inert -- no marker, no grant, default budget
# for every subagent in the session. Nominally the transcript lives at
# `<dirname of scratchpad_dir>/tasks/<agent_id>.output`, a symlink to
# `<config>/projects/<project-slug>/<session>/subagents/agent-<agent_id>.jsonl`.
# Verified 2026-09-16 directly against a running session's own live
# state (ls/readlink on its real `tasks/` dir); the resolved file's first
# `type: "user"` entry has a plain-string `.message.content`, consistent
# with it being the agent's brief, and is a wholly separate, much smaller
# file from the main session transcript -- not the same thing `transcript_path`
# names for the coordinator.
#
# Only calls carrying an `agent_id` are gated (i.e. calls made by a
# subagent); the coordinator's own tool calls are never counted or gated
# here.
#
# Over budget: deny, telling the agent it has hit a CHECKPOINT, not a
# failure -- stop calling any tool other than SubagentHandback or
# SendMessage, report where it got to, what remains, and what it estimates
# it still needs, and leave the work resumable, since the coordinator may
# send it back with a larger budget. Both SubagentHandback and SendMessage
# stay permitted at every budget state on every path here, including every
# error path: this is the non-negotiable lesson recorded in
# ~/.claude/CLAUDE.md -- a denial that tells an agent to report back must
# never also deny the tools it reports back WITH, or the agent loops burning
# its remaining turn on a refused report.#
# Neither of those two tools is guaranteed to BE in a given subagent's
# toolset, though. A live campaign found three Sonnet assistants that had
# neither: each reported both as unavailable and then went silent, and none
# of them wrote the evidence file it was carrying. So the denial text names
# a third route that depends on no tool at all -- stop calling tools and
# write the report as the final text message, which is what reaches the
# coordinator anyway.
#
# Safety: set -u (not -e). Fails OPEN -- on empty/unparseable stdin, a call
# with no agent_id, a missing/unreadable transcript, no marker, a malformed
# marker, any jq failure (including inside the grant scan -- a jq failure
# there just means "no grant found", never a denial caused by the failure
# itself), or lock contention -- falling back through env var then built-in
# default rather than ever blocking. Never hangs: lock wait capped at
# ~200ms total.
set -u

# Overridable only so this script can be exercised by a test harness without
# writing into the live counters of real, currently-running agents (see
# claude-hooks/agent_token_budget.sh for the identical convention). Unset in
# the real wired invocation, so production behaviour is unchanged.
STATE_DIR="${CLAUDE_CALL_BUDGET_STATE_DIR:-${HOME}/.claude/state/spawn-count}"
LOG="$STATE_DIR/call-budget-log.tsv"
mkdir -p "$STATE_DIR" 2>/dev/null

input="$(cat 2>/dev/null)"
[ -z "$input" ] && exit 0

session_id="$(printf '%s' "$input" | jq -r '.session_id // empty' 2>/dev/null)"
if [ $? -ne 0 ]; then
    exit 0
fi
[ -z "$session_id" ] && session_id="unknown"

agent_id="$(printf '%s' "$input" | jq -r '.agent_id // empty' 2>/dev/null)"
if [ $? -ne 0 ]; then
    exit 0
fi

tool_name="$(printf '%s' "$input" | jq -r '.tool_name // empty' 2>/dev/null)"
if [ $? -ne 0 ]; then
    exit 0
fi

# Not a subagent call: this hook never gates the coordinator's own calls.
if [ -z "$agent_id" ]; then
    exit 0
fi

# scratchpad_dir is optional-effort only (it locates the transcript for the
# marker lookup) -- a jq hiccup on THIS field must not abort the core
# counting/denial logic the way a hiccup on the three fields above does, so
# a failure here just means "no marker available", not "pass through".
scratchpad_dir="$(printf '%s' "$input" | jq -r '.scratchpad_dir // empty' 2>/dev/null)"
if [ $? -ne 0 ]; then
    scratchpad_dir=""
fi

# transcript_path is the same kind of optional-effort field: a second way to
# reach the agent's own transcript when the tasks symlink is not where
# scratchpad_dir says it should be. See resolve_transcript().
transcript_path="$(printf '%s' "$input" | jq -r '.transcript_path // empty' 2>/dev/null)"
if [ $? -ne 0 ]; then
    transcript_path=""
fi

# Integer-validation shape shared by the marker and the env var: plain
# digits, 1-15 of them. The upper bound is not arbitrary: bash's
# `[ -gt ]` does signed 64-bit arithmetic, and a digit string long enough to
# overflow that makes the comparison error out instead of compare cleanly --
# 15 digits (max ~10^15) is already far beyond any plausible call budget
# while staying safely inside 64-bit range, so this rejects the overflow
# case (an "absurd" marker) as invalid rather than letting it break the
# later comparison.
is_valid_budget() {
    [[ "$1" =~ ^[0-9]{1,15}$ ]]
}

DEFAULT_BUDGET=80

# Hard maximum call count (2026-09-17). Until now a marker or a coordinator
# grant was an absolute ceiling with NO upper bound -- any brief that merely
# quotes a marker as an example, or any coordinator, could set an
# arbitrarily large number and the hook would obey it. This is the backstop
# no marker, no grant and no env var can exceed. Measured 2026-09-16
# alongside DEFAULT_BUDGET's own distribution: p99 is 285 calls, so 500
# kills roughly the top half percent of agents outright regardless of what
# any source claims their budget should be.
MAX_CALL_BUDGET=500

# The compiled maximum above may only be LOWERED by this env var, never
# raised -- read it, validate it, and use it only when it is strictly less
# than MAX_CALL_BUDGET. A value that is invalid or >= the compiled constant
# is silently ignored and the constant stands. Raising the real ceiling
# requires editing this file, a deliberate act by a human.
effective_max_call_budget="$MAX_CALL_BUDGET"
if [ -n "${CLAUDE_AGENT_CALL_BUDGET_MAX:-}" ] && is_valid_budget "${CLAUDE_AGENT_CALL_BUDGET_MAX}" && [ "${CLAUDE_AGENT_CALL_BUDGET_MAX}" -lt "$MAX_CALL_BUDGET" ]; then
    effective_max_call_budget="$CLAUDE_AGENT_CALL_BUDGET_MAX"
fi

# clamp_budget VALUE -- the ONE clamp, called at EVERY point a candidate
# budget (brief marker, grant, env var, or the default) is finalized, so no
# source can bypass it. Sets CLAMP_RESULT to min(VALUE, effective max) and
# CLAMP_REQUESTED to VALUE when VALUE actually exceeded the max (empty
# otherwise) -- the caller must read both immediately, since the next call
# to this function overwrites them. Also sets the sticky any_clamped=1 flag
# (never reset back to 0 within one invocation) so the log line for this
# call can record that clamping happened somewhere in resolving it.
any_clamped=0
clamp_budget() {
    local v="$1"
    CLAMP_REQUESTED=""
    if [ "$v" -gt "$effective_max_call_budget" ]; then
        CLAMP_RESULT="$effective_max_call_budget"
        CLAMP_REQUESTED="$v"
        any_clamped=1
    else
        CLAMP_RESULT="$v"
    fi
}

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

# find_latest_grant TRANSCRIPT -- prints the digits of the LATEST valid
# [[CALL_BUDGET=N]] marker found in a coordinator grant (either shape, see
# GRANTS in the header), or prints nothing if none is found. Never fails the
# caller: any jq error or malformed line just yields no output, i.e. no
# grant, i.e. the cached budget stands -- fail-open by construction. Mirrors
# agent_token_budget.sh's function of the same name exactly, except for the
# marker token (CALL_BUDGET here, TOKEN_BUDGET there).
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
            [scan("\\[\\[CALL_BUDGET=([0-9]+)\\]\\]")] | if length>0 then .[0][0] else "" end
        ' 2>/dev/null)"
        if [ -n "$candidate" ] && is_valid_budget "$candidate"; then
            marker="$candidate"
        fi
    done <<< "$raw"
    printf '%s' "$marker"
}

safe_agent_id="$(printf '%s' "$agent_id" | tr -c 'A-Za-z0-9_-' '_')"
counter_file="$STATE_DIR/calls-${session_id}-${safe_agent_id}"
budget_file="$STATE_DIR/budget-${session_id}-${safe_agent_id}"
lock_dir="$STATE_DIR/.calls-${session_id}-${safe_agent_id}.lock"

locked=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
    if mkdir "$lock_dir" 2>/dev/null; then
        locked=1
        break
    fi
    sleep 0.02
done

# Resolve the transcript path unconditionally (not just on a cache miss):
# the grant scan below needs it on EVERY call, including one where the
# brief-marker cache already has an answer.
transcript="$(resolve_transcript "$scratchpad_dir" "$transcript_path" "$agent_id" 2>/dev/null)"
[ -z "${transcript:-}" ] && transcript=""

# --- resolve budget + its source (once per agent_id, cached) --------------
# Cache format is "<clamped budget>\t<source>\t<raw requested, or empty>" --
# the CLAMPED value is what is cached (2026-09-17 clamp fix), so the cache
# and the enforced ceiling can never disagree; budget_requested carries the
# pre-clamp figure forward across calls so a denial many calls later can
# still name both numbers. A 2-field legacy cache line (written before this
# field existed) reads back with budget_requested empty, i.e. "not clamped"
# -- the correct assumption for a value written before clamping existed.
budget=""
budget_source=""
budget_requested=""
if [ -f "$budget_file" ]; then
    raw_line="$(cat "$budget_file" 2>/dev/null)"
    IFS=$'\t' read -r raw_budget raw_source raw_requested <<< "$raw_line"
    if is_valid_budget "$raw_budget" && [ -n "$raw_source" ]; then
        budget="$raw_budget"
        budget_source="$raw_source"
        budget_requested="$raw_requested"
    fi
fi

if [ -z "$budget" ]; then
    marker_val=""
    if [ -n "$transcript" ]; then
        # ONLY the first user-role entry in the WHOLE transcript -- the
        # brief -- and nothing appended after it. This is the line the
        # self-raise guarantee rests on: see SECURITY above.
        first_user_text="$(jq -c 'select(.type=="user")' "$transcript" 2>/dev/null | head -1 | jq -r '
            .message.content
            | if type=="string" then .
              elif type=="array" then ([.[]? | select(.type=="text") | .text] | join("\n"))
              else "" end
        ' 2>/dev/null)"
        if [ -n "$first_user_text" ]; then
            candidate="$(printf '%s' "$first_user_text" | grep -oE '\[\[CALL_BUDGET=[0-9]+\]\]' | head -1 | grep -oE '[0-9]+')"
            if [ -n "$candidate" ] && is_valid_budget "$candidate"; then
                marker_val="$candidate"
            fi
        fi
    fi

    if [ -n "$marker_val" ]; then
        raw_candidate="$marker_val"
        budget_source="brief"
    elif [ -n "${CLAUDE_AGENT_CALL_BUDGET:-}" ] && is_valid_budget "${CLAUDE_AGENT_CALL_BUDGET}"; then
        raw_candidate="$CLAUDE_AGENT_CALL_BUDGET"
        budget_source="env"
    elif [ -z "$transcript" ]; then
        # No transcript means the marker was never LOOKED for, which is a
        # different fact from "the brief carried no marker" -- and the one
        # that cost a day to find on 2026-09-21. Name it in the log column
        # so the next occurrence is one grep away.
        raw_candidate="$DEFAULT_BUDGET"
        budget_source="default-notranscript"
    else
        raw_candidate="$DEFAULT_BUDGET"
        budget_source="default"
    fi
    clamp_budget "$raw_candidate"
    budget="$CLAMP_RESULT"
    budget_requested="$CLAMP_REQUESTED"

    printf '%s\t%s\t%s\n' "$budget" "$budget_source" "$budget_requested" > "$budget_file" 2>/dev/null
    # A failed cache write just means next call re-resolves from scratch --
    # wasted work, not a correctness problem, so no error handling needed.
fi

count=0
if [ -f "$counter_file" ]; then
    raw="$(cat "$counter_file" 2>/dev/null)"
    [[ "$raw" =~ ^[0-9]+$ ]] && count="$raw"
fi
new_count=$((count + 1))
printf '%s\n' "$new_count" > "$counter_file" 2>/dev/null

# --- conditional grant scan (2026-09-17: the checkpoint-not-kill-switch
# fix) -- only runs once the incremented count has reached or passed the
# cached budget; see GRANTS / CONDITIONAL SCAN in the header. A grant found
# here REPLACES the ceiling (never resets new_count) and is written back
# into the cache so the next call resolves it without rescanning.
if [ -n "$transcript" ] && [ "$new_count" -ge "$budget" ]; then
    grant_val="$(find_latest_grant "$transcript")"
    if [ -n "$grant_val" ] && is_valid_budget "$grant_val"; then
        clamp_budget "$grant_val"
        budget="$CLAMP_RESULT"
        budget_source="grant"
        budget_requested="$CLAMP_REQUESTED"
        printf '%s\t%s\t%s\n' "$budget" "$budget_source" "$budget_requested" > "$budget_file" 2>/dev/null
    fi
fi

[ "$locked" -eq 1 ] && rmdir "$lock_dir" 2>/dev/null

timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null)"
[ -z "$timestamp" ] && timestamp="unknown-time"

clamp_suffix=""
[ "$any_clamped" -eq 1 ] && clamp_suffix="-CLAMPED"

if [ "$new_count" -gt "$budget" ]; then
    # Same reasoning as agent_spawn_cap.sh: this denial TELLS the agent to report
    # back, so denying the tools it reports back WITH makes that impossible, and
    # the agent burns the rest of its turn retrying a refused report. Filing a
    # report is not "finishing the remaining work" -- SubagentHandback ends the
    # agent, SendMessage only carries text to the coordinator -- so both stay
    # permitted past the budget while every other tool is denied.
    case "$tool_name" in
        SubagentHandback|SendMessage)
            printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$timestamp" "$session_id" "$agent_id" "$new_count" "$budget" "$budget_source" "ALLOW-REPORT${clamp_suffix}" >> "$LOG" 2>/dev/null
            exit 0
            ;;
    esac
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$timestamp" "$session_id" "$agent_id" "$new_count" "$budget" "$budget_source" "BLOCK${clamp_suffix}" >> "$LOG" 2>/dev/null
    jq -n --arg count "$new_count" --arg budget "$budget" --arg requested "$budget_requested" '
      (if ($requested != "") then
         (" A budget of " + $requested + " calls was requested, but this hook enforces a hard maximum, so the ceiling in force here is " + $budget + ", not " + $requested + ".")
       else "" end) as $clampclause
      | ((($count | tonumber) + 40) | floor | tostring) as $suggest
      | ("You have hit a tool-call budget CHECKPOINT, not a failure (" + $count + " of " + $budget + " calls used for this subagent)." + $clampclause + " This is a pause in the work, not the end of it. Every tool is now denied to you EXCEPT SubagentHandback and SendMessage -- stop calling anything else right now. Call SubagentHandback once and report honestly: where you got to, what remains, and what you estimate you still need to finish. If SubagentHandback is not in your toolset, which is what happens whenever this session is not in auto mode, report with SendMessage instead and do not retry SubagentHandback. If NEITHER of those two tools is in your toolset, stop calling tools altogether and write your report as your final text message -- that text is what reaches your coordinator, it needs no tool at all, and it is the one route that always exists. Going quiet is the worst outcome available to you here, because all your coordinator sees is an agent that ended without delivering anything. Name any file you were told to write and did not get to, because your coordinator can have it written, but only if it knows it is missing. Leave your work resumable -- do not treat this as the final report unless it genuinely is one. Your coordinator can read this and send you back with a larger budget, and that grant will be read from your transcript on your next call, so either tool is a safe way to report here. IMPORTANT, so that you ask for a number that actually helps: a grant is an ABSOLUTE ceiling on your CUMULATIVE call count, not a fresh allowance. Your count of " + $count + " is never reset by a grant, so a grant must be LARGER than " + $count + " to buy you any room at all, and a grant of N leaves you only N minus " + $count + " further calls. Tell your coordinator the TOTAL ceiling you need, never an increment: if you estimate roughly 40 more calls of work, ask for about " + $suggest + ", not 40.") as $reason
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

printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$timestamp" "$session_id" "$agent_id" "$new_count" "$budget" "$budget_source" "ALLOW${clamp_suffix}" >> "$LOG" 2>/dev/null
exit 0
