#!/usr/bin/env bash
# Takes Sam's hooks afresh from a runaway-agent checkout: refresh.sh <checkout> [<commit>, origin/main if none]
# Each is his file at that commit as `git show` gives it (LF, whatever core.autocrlf did to his checkout), with
# "the controller" in Sam's place, as his own install.sh --owner puts its user's name in: a worker answers to the
# controller, not to Sam. A dated ruling ("Sam, 2026-10-12") stays his. Nothing else is edited; what pilot needs
# differently is settled in kit.sh, before his hook runs.
set -euo pipefail
src="${1:?usage: refresh.sh <runaway-agent checkout> [<commit>]}"
rev="${2:-origin/main}"
dir="$(dirname "${BASH_SOURCE[0]}")/runaway"
for f in agent_token_budget.sh big_read_guard.sh fat_result_notice.sh agent_call_budget.sh agent_ratelimit_gate.sh \
         handback_report_gate.sh; do
    git -C "$src" show "$rev:$f" | perl -pe 's/\bSam\b(?!,? \(?\d{4}-\d\d-\d\d)/the controller/g' > "$dir/$f"
done
echo "runaway-agent $(git -C "$src" log -1 --format='%h (%ad)' --date=short "$rev"): kit.sh and README.md name it"
