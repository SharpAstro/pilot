#!/usr/bin/env bash
# PreToolUse hook: gate `Agent` spawns on the ACCOUNT's real remaining
# rate-limit window, and tell the spawner where that window stands.
#
# WHY THIS EXISTS. agent_call_budget.sh and agent_token_budget.sh cap each
# subagent INDIVIDUALLY. Nothing caps the tree. A 12.5-hour mission-runner
# session burned 20.1M weighted tokens while, as far as those two hooks could
# tell, every single agent in it complied. The only signal that is about the
# whole tree rather than one agent is the account's own 5-hour window, and the
# only place that number appears is the status line's stdin.
#
# So the status line caches `rate_limits` (see the cache block in
# statusline-command.sh) and this hook reads it. No cache, stale cache, or any
# jq failure is FAIL-OPEN: a spawn is never blocked because the plumbing broke.
#
# TWO WINDOWS, TWO ACTIONS -- they are different jobs:
#   five_hour  is the throttle. You hit it by going hard in one session and it
#              clears in hours. Below the floor: stop spawning, wind down, and
#              it is safe to resume after `resets_at`.
#   seven_day  is the budget. No amount of waiting inside a week gets it back,
#              so below its floor the answer is never "resume automatically",
#              it is "stop and tell Sam". Clearing the throttle still spends
#              the budget, which is exactly how an unattended resume empties a
#              week at three in the morning.
#
# ENV:
#   CLAUDE_RATE_LIMIT_CACHE           cache path (default $CLAUDE_CONFIG_DIR/rate-limits.json)
#   CLAUDE_AGENT_RATELIMIT_FLOOR      5-hour % remaining below which spawns are denied (default 10)
#   CLAUDE_AGENT_RATELIMIT_WEEKLY_FLOOR  7-day % remaining below which spawns are denied (default 5)
#   CLAUDE_AGENT_RATELIMIT_WARN_BAND  extra % above the floor that warns (default 15)
#   CLAUDE_AGENT_RATELIMIT_MAX_AGE    seconds before the cache is stale (default 900)
#   CLAUDE_AGENT_RATELIMIT_OFF        any non-empty value disables the gate entirely
set -u

[ -n "${CLAUDE_AGENT_RATELIMIT_OFF:-}" ] && exit 0
command -v jq >/dev/null 2>&1 || exit 0

input=$(cat 2>/dev/null) || exit 0
[ -z "$input" ] && exit 0

tool_name="$(printf '%s' "$input" | jq -r '.tool_name // ""' 2>/dev/null)" || exit 0
[ "$tool_name" = "Agent" ] || exit 0

# Profile-scoped, not $HOME-scoped. Each config dir carries its own login --
# ~/.claude.json and ~/.claude-work/.claude.json are different accounts with
# different rate-limit windows -- so a cache at a fixed $HOME path would let one
# profile's session gate spawns on another account's numbers. The status line
# had exactly this bug against the same file and fixed it the same way.
CACHE="${CLAUDE_RATE_LIMIT_CACHE:-${CLAUDE_CONFIG_DIR:-$HOME/.claude}/rate-limits.json}"
[ -r "$CACHE" ] || exit 0

FLOOR="${CLAUDE_AGENT_RATELIMIT_FLOOR:-10}"
WEEKLY_FLOOR="${CLAUDE_AGENT_RATELIMIT_WEEKLY_FLOOR:-5}"
WARN_BAND="${CLAUDE_AGENT_RATELIMIT_WARN_BAND:-15}"
MAX_AGE="${CLAUDE_AGENT_RATELIMIT_MAX_AGE:-900}"

cache="$(cat "$CACHE" 2>/dev/null)" || exit 0
[ -z "$cache" ] && exit 0

written_at="$(printf '%s' "$cache" | jq -r '.written_at // empty' 2>/dev/null)"
[ -z "$written_at" ] && exit 0
now="$(date +%s 2>/dev/null)" || exit 0
age=$(( now - written_at ))
[ "$age" -lt 0 ] && exit 0
[ "$age" -gt "$MAX_AGE" ] && exit 0

five_used="$(printf '%s' "$cache" | jq -r '.rate_limits.five_hour.used_percentage // empty' 2>/dev/null)"
week_used="$(printf '%s' "$cache" | jq -r '.rate_limits.seven_day.used_percentage // empty' 2>/dev/null)"
resets_at="$(printf '%s' "$cache" | jq -r '.rate_limits.five_hour.resets_at // empty' 2>/dev/null)"

# No 5-hour figure at all means no API response has landed yet this session.
[ -z "$five_used" ] && exit 0

five_left="$(awk -v u="$five_used" 'BEGIN { printf "%.0f", 100 - u }' 2>/dev/null)"
[ -z "$five_left" ] && exit 0
if [ -n "$week_used" ]; then
    week_left="$(awk -v u="$week_used" 'BEGIN { printf "%.0f", 100 - u }' 2>/dev/null)"
else
    week_left=""
fi

# ---- weekly floor: stop, and do not offer a timer -------------------------
if [ -n "$week_left" ] && [ "$week_left" -le "$WEEKLY_FLOOR" ]; then
    jq -n --arg w "$week_left" --arg f "$WEEKLY_FLOOR" '
      {
        hookSpecificOutput: {
          hookEventName: "PreToolUse",
          permissionDecision: "deny",
          permissionDecisionReason: ("Agent spawn denied: the 7-DAY allowance is at " + $w + "% remaining, at or below the floor of " + $f + "%. This is the budget, not the throttle -- waiting does not get it back, and spawning more agents is the fastest way to spend what is left. Do not retry, do not spawn a smaller agent instead, and do not schedule a resume. Stop starting new work now, finish or checkpoint what is already running, write the pickup note so the next session can resume from disk, and tell Sam plainly that the weekly window is what stopped you. Whether to spend the remainder is his call, not yours.")
        },
        systemMessage: ("Agent spawn blocked -- 7-day allowance at " + $w + "% remaining (floor " + $f + "%).")
      }' 2>/dev/null
    exit 0
fi

# ---- 5-hour floor: stop, and a resume after resets_at is legitimate -------
if [ "$five_left" -le "$FLOOR" ]; then
    jq -n --arg r "$five_left" --arg f "$FLOOR" --arg t "${resets_at:-unknown}" --arg w "${week_left:-unknown}" '
      (if $t == "unknown" then "" else (" The window resets at " + $t + ".") end) as $when
      | (if $w == "unknown" then "" else (" The 7-day allowance is at " + $w + "% remaining, so a resume after the reset is drawing on that -- re-read it before re-entering rather than charging back into a fan-out.") end) as $weekly
      | {
          hookSpecificOutput: {
            hookEventName: "PreToolUse",
            permissionDecision: "deny",
            permissionDecisionReason: ("Agent spawn denied: the 5-hour window is at " + $r + "% remaining, at or below the floor of " + $f + "%." + $when + " This is a CHECKPOINT for the whole tree, not a failure of this one spawn, and it is the one cap that sees the tree -- the per-agent budgets cannot. Do not retry and do not route the work to an already-running agent to get around it. Finish or checkpoint what is in flight, write the pickup note to disk so the work resumes rather than restarts, and report to Sam where the mission stands." + $weekly)
          },
          systemMessage: ("Agent spawn blocked -- 5-hour window at " + $r + "% remaining (floor " + $f + "%).")
        }' 2>/dev/null
    exit 0
fi

# ---- healthy: allow, and always tell the spawner where it stands ----------
warn_at=$(( FLOOR + WARN_BAND ))
jq -n --arg r "$five_left" --arg f "$FLOOR" --arg w "${week_left:-unknown}" --arg warn "$warn_at" '
  (if $w == "unknown" then "" else (", 7-day at " + $w + "%") end) as $weekly
  | (if (($r | tonumber) <= ($warn | tonumber))
     then " You are inside the warning band: spawns stop entirely at " + $f + "%. Prefer finishing work already in flight over starting a new branch of the tree, and make sure anything running can be resumed from disk."
     else "" end) as $note
  | {
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        additionalContext: ("Account window at spawn time: 5-hour at " + $r + "% remaining" + $weekly + "." + $note)
      }
    }' 2>/dev/null
exit 0
