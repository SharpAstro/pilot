# PIL433: what reviews cost, before this flight

Figures for the PR of pilot #433, to quote beside the after figures once flights have landed on the new reviews.

## What was measured

For each flight that landed from 2026-10-22 to 2026-10-29 (both days counted, UTC) on this node (de1): its reviewer
sessions, and their tokens as `Transcripts.cs` reads them. Tokens are summed over each session's transcript and its
subagents' files, a message counted once by its id, so a deep review's subagents are in the figure. The weighted spend
is the one `SessionRow.SpendWeighted` uses: input + 1.25 x cache creation + 0.1 x cache read + 5 x output.

## How it was made

```
pilot-node review-cost --since 2026-10-22 --until 2026-10-29
pilot review-cost --since 2026-10-22 --until 2026-10-29        # the same report, through a node that has the verb
```

The first form is the one used: it reads the data folder (`%LOCALAPPDATA%\pilot2\store`: the snapshot, then the
journal after it) read-only, with no node started, and the transcripts under `~/.claude/projects`. The live node is an
older build without the verb and was neither restarted nor replaced. The run was on 2026-10-30.

A flight's reviewer sessions are the sessions with the reviewer role on it that the node still lists, plus every
transcript in the flight's worktree project folder whose title (Claude Code's `custom-title`) is a
`<machine>-<code>-review` name. The second source is needed because ended sessions leave the node's rows: from the
live snapshot alone only 36 sessions of 91 flights were found, and PIL353 none. Sessions are numbered as rounds in the
order they started.

## Before

| | |
|---|---|
| Landed flights | 91 (89 with a reviewer session; 2 with none found) |
| Reviewer sessions | 298 (3.3 per reviewed flight) |
| Input tokens | 12,665 |
| Cache creation | 39,925,197 |
| Cache read | 833,505,761 |
| Output | 5,193,869 |
| Weighted spend | 159,238,940 |
| Average per reviewed flight | 3.3 sessions, weighted 1,789,201 (cache creation 448,597, cache read 9,365,233, output 58,358) |

By the model the session ran on (7 sessions have no model and no tokens: a transcript with no reply in it, or none on disk):

| Model | Sessions | Weighted spend |
|---|---|---|
| claude-opus-5-5 | 258 | 92,494,595 |
| claude-sonnet-5-5 | 33 | 66,744,345 |

Sessions per flight: 1 session 26 flights, 2 sessions 19, 3 sessions 11, 4 sessions 12, 5 sessions 9, 6 sessions 2,
7 sessions 3, 8 or more 7. 33 flights had more than three sessions, 102 sessions in all beyond a third.

A first round averaged 370,916 weighted (89 sessions) and a later round 606,862 (208 sessions): a re-review cost more
than the review it followed, which is what this flight changes.

The flights the issue names (sessions, weighted spend):

| Flight | PR | Reviewer sessions | Weighted spend |
|---|---|---|---|
| PIL353 | pilot #373 | 17 | 7,101,742 |
| TIA1879 | tianwen #1888 | 9 | 5,839,349 |
| PIL355 | pilot #374 | 11 | 4,012,059 |
| TIA1867 | tianwen #1873 | 5 | 3,127,718 |
| TIA1878 | tianwen #1885 | 5 | 2,799,760 |
| PIL416 | pilot #420 | 6 | 2,132,384 |
| TIA1871 | tianwen #1872 | 5 | 1,967,997 |
| DIR173 | DIR.Lib #174 | 3 | 545,048 |

The dearest flights of the window were TIA1839 (7 sessions, 28,486,076, on Sonnet), TIA1911 (7, 17,191,900, on
Sonnet), PIL468 (5, 9,600,970, on Sonnet) and then PIL353.

## Reading them

- A session is a Claude session, not a verdict. The issue counts PIL353 at 8 review rounds; 17 reviewer sessions were
  found for it, so some sessions ended without a verdict (restarted, woken or resumed). The figure that costs tokens is
  the session count, and it is what the after figures count too, so before and after compare like with like.
- Opus carries the bulk of the spend (58%) in the window. The three dearest flights ran on Sonnet, so the model alone
  does not make a review cheap: how much each session reads does, and a Sonnet session is not cheap when it reads the
  whole PR again.
- One session, the twelfth of PIL402, is listed by the node with no transcript on disk; its row's own spend (0) is used.
- Heads are not in the before figures: the rounds the node keeps (`ReviewRounds`) came with this flight, so a flight
  landed before it shows `head ?`.

## After

To fill in once flights have landed on the new reviews, with the same command over a window that starts then:

```
pilot review-cost --since <date the new build ran> [--until <date>]
```

and quote the totals, the average per reviewed flight, and the sessions per flight beside the table above.
