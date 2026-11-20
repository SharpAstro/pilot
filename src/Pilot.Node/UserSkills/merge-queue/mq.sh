#!/usr/bin/env bash
# mq.sh -- where a PR stands with respect to a GitHub merge queue, in one line.
#
#   mq.sh status <pr> [owner/repo]   one line: MERGED / QUEUED / AUTO / OPEN / CLOSED, with the facts behind it
#   mq.sh queue  [owner/repo]        the queue on main, in order
#   mq.sh watch  <pr> [owner/repo]   poll every 60 s until the PR is MERGED or CLOSED (exit 0), leaves the queue unmerged
#                                    (exit 1), or five polls in a row fail (exit 3: state unknown)
#   mq.sh enqueue <pr> [owner/repo]  put the PR in the queue (GraphQL enqueuePullRequest), then print its status.
#                                    GitHub refuses it until every required check has reported. pilot's merge guard
#                                    refuses it from a session the node started; the person's own session may run it.
#   mq.sh ready <pr> [owner/repo]    "Merge when ready": try enqueue every 120 s until GitHub takes it, then watch.
#                                    exit 0 merged; 1 a check failed, the PR closed or it left the queue; 3 state
#                                    unknown; 4 not queued after 2 hours. Ends itself: run it in the background.
#
# Why enqueue is here: `gh pr merge N` does NOT join a queue on every repo. gh 2.86 answered a green, CLEAN PR on
# tianwen (#2038, 2026-11-07) with "Auto merge is not allowed for this repository (enablePullRequestAutoMerge)":
# it asked for auto-merge, which that repo has switched off, and never tried the queue.
#
# Why a script: `gh pr view --json` has NO field for queue membership (mergeStateStatus CLEAN and
# autoMergeRequest null both describe a PR that is sitting in the queue), so a PR in the queue reads
# as "open, nothing pending". The queue entry is GraphQL only (PullRequest.mergeQueueEntry), and
# the timeline events added_to_merge_queue / removed_from_merge_queue say what happened to it.
set -euo pipefail

cmd=${1:?usage: mq.sh status|queue|watch|enqueue|ready <pr> [owner/repo]}
repo_arg=""
pr=""
case "$cmd" in
  status|watch|enqueue|ready) pr=${2:?pr number}; repo_arg=${3:-} ;;
  queue) repo_arg=${2:-} ;;
  *) echo "unknown command: $cmd" >&2; exit 2 ;;
esac
repo=${repo_arg:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}
owner=${repo%%/*}; name=${repo##*/}

status() {
  gh api graphql -f query='
    query($o:String!,$n:String!,$p:Int!){repository(owner:$o,name:$n){pullRequest(number:$p){
      state mergedAt baseRefName mergeStateStatus autoMergeRequest{enabledAt}
      mergeQueueEntry{state position enqueuedAt estimatedTimeToMerge}
      commits(last:1){nodes{commit{statusCheckRollup{state}}}}}}}' \
    -f o="$owner" -f n="$name" -F p="$1" --jq '
    .data.repository.pullRequest as $p
    | ($p.commits.nodes[0].commit.statusCheckRollup.state // "NONE") as $checks
    | if $p.state == "MERGED" then "MERGED at \($p.mergedAt)"
      elif $p.state == "CLOSED" then "CLOSED (not merged)"
      elif $p.mergeQueueEntry then
        "QUEUED position \($p.mergeQueueEntry.position) state \($p.mergeQueueEntry.state) since \($p.mergeQueueEntry.enqueuedAt)"
        + (if $p.mergeQueueEntry.estimatedTimeToMerge then " eta \($p.mergeQueueEntry.estimatedTimeToMerge)s" else "" end)
      elif $p.autoMergeRequest then "AUTO (enters the queue when required checks pass; since \($p.autoMergeRequest.enabledAt)) checks \($checks)"
      else "OPEN not queued; mergeState \($p.mergeStateStatus) checks \($checks)" end'
}

enqueue() {
  local id
  id=$(gh api graphql -f query='
    query($o:String!,$n:String!,$p:Int!){repository(owner:$o,name:$n){pullRequest(number:$p){id}}}'     -f o="$owner" -f n="$name" -F p="$1" --jq .data.repository.pullRequest.id)
  gh api graphql -f query='
    mutation($id:ID!){enqueuePullRequest(input:{pullRequestId:$id}){mergeQueueEntry{position}}}'     -f id="$id" --jq '"queued at position \(.data.enqueuePullRequest.mergeQueueEntry.position)"'
}

case "$cmd" in
  status) status "$pr" ;;
  enqueue) enqueue "$pr"; status "$pr" ;;
  ready)
    # The web page's "Merge when ready" has no API where the repo has auto-merge off: enqueue is refused until every
    # required check has reported, so it is tried until it is taken. A refusal is expected and is not printed each time.
    end=$((SECONDS + 7200))
    while :; do
      line=$(status "$pr" 2>&1) || line="poll failed: $line"
      case "$line" in
        MERGED*) echo "$line"; exit 0 ;;
        CLOSED*) echo "$line"; exit 1 ;;
        QUEUED*|AUTO*) echo "$(date -u +%H:%M:%SZ) $line"; break ;;
        *"checks FAILURE"*|*"checks ERROR"*) echo "not queued, a check failed: $line"; exit 1 ;;
      esac
      if said=$(enqueue "$pr" 2>&1); then
        echo "$(date -u +%H:%M:%SZ) $said"
        break
      fi
      if [ "$SECONDS" -ge "$end" ]; then
        echo "not queued after 2 hours; GitHub's last answer: $said" >&2
        exit 4
      fi
      sleep 120
    done
    exec bash "$0" watch "$pr" "$repo" ;;
  queue)
    gh api graphql -f query='
      query($o:String!,$n:String!){repository(owner:$o,name:$n){mergeQueue(branch:"main"){
        entries(first:50){nodes{position state enqueuedAt pullRequest{number title}}}}}}' \
      -f o="$owner" -f n="$name" --jq '
      .data.repository.mergeQueue.entries.nodes
      | if length == 0 then "queue empty" else .[] | "\(.position) #\(.pullRequest.number) \(.state) \(.enqueuedAt) \(.pullRequest.title)" end' ;;
  watch)
    seen_queue=0
    failed=0
    while :; do
      # One failed poll (a network blip, a rate limit, a 5xx) is not an answer: say it and poll again. Five in a row
      # end the watch with exit 3, never 1, which means the PR left the queue.
      if ! line=$(status "$pr" 2>&1); then
        failed=$((failed + 1))
        echo "$(date -u +%H:%M:%SZ) poll failed ($failed in a row): $line" >&2
        if [ "$failed" -ge 5 ]; then
          echo "gave up: $failed polls in a row failed; the PR's state is unknown" >&2
          exit 3
        fi
        sleep 60
        continue
      fi
      failed=0
      echo "$(date -u +%H:%M:%SZ) $line"
      case "$line" in
        MERGED*|CLOSED*) exit 0 ;;
        QUEUED*) seen_queue=1 ;;
        *) if [ "$seen_queue" = 1 ]; then
             echo "left the queue without merging; last removal reason:"
             gh api "repos/$repo/issues/$pr/timeline?per_page=100" --jq \
               '[.[]|select(.event=="removed_from_merge_queue")]|last|"\(.created_at) \(.event)"'
             exit 1
           fi ;;
      esac
      sleep 60
    done ;;
esac
