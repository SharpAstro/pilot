#!/usr/bin/env bash
# Runs one of Sam's runaway-agent hooks for a worker, under Git Bash on Windows: kit.sh <hook>.sh
# The kit is the sibling checkout ../runaway-agent, used in place so its fixes arrive with a git pull.
# His hooks need jq, which winget puts in its Links dir; a Claude Code daemon started before jq was
# installed does not have that on PATH, and a hook without jq fails open, silently.
links="$(cygpath -u "${LOCALAPPDATA:-$USERPROFILE/AppData/Local}")/Microsoft/WinGet/Links"
case ":$PATH:" in *":$links:"*) ;; *) PATH="$links:$PATH" ;; esac
kit="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)/runaway-agent"
if [ ! -f "$kit/$1" ]; then
    echo "pilot: no runaway-agent hook at $kit/$1" >&2
    exit 1
fi

# big_read_guard.sh measures a Read in bytes and takes only offset/limit as a slice. Read takes a PDF by
# `pages` and downscales an image, so neither is a byte count read into context: an ephemeris worker was
# refused a 2 MB PDF and told to use offset/limit, which a PDF does not take. Those are settled here;
# everything else goes to Sam's hook as it is.
if [ "$1" = "big_read_guard.sh" ]; then
    input="$(cat)"
    if [ "$(printf '%s' "$input" | jq -r '.tool_name // empty' 2>/dev/null)" = "Read" ]; then
        path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty' 2>/dev/null)"
        pages="$(printf '%s' "$input" | jq -r '.tool_input.pages // empty' 2>/dev/null)"
        case "${path,,}" in
            *.pdf)
                [ -n "$pages" ] && exit 0
                size="$(stat -c%s -- "$path" 2>/dev/null)"
                if [[ "$size" =~ ^[0-9]+$ ]] && [ "$size" -gt "${CLAUDE_BIG_READ_THRESHOLD_BYTES:-100000}" ]; then
                    jq -n --arg path "$path" --arg size "$size" '{hookSpecificOutput: {hookEventName: "PreToolUse",
                      permissionDecision: "deny", permissionDecisionReason: ("\($path) is a \($size)-byte PDF: read it a few pages at a time with the `pages` parameter (e.g. \"1-5\", at most 20 per call), not whole and not with offset/limit.")}}'
                fi
                exit 0 ;;
            *.png|*.jpg|*.jpeg|*.gif|*.webp|*.bmp) exit 0 ;;
        esac
    fi
    printf '%s' "$input" | bash "$kit/$1"
    exit $?
fi
exec bash "$kit/$1"
