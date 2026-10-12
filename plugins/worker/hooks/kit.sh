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
exec bash "$kit/$1"
