# PIL907: Chat frame times, 20 vs 2,000 entries

The Chat page keeps one row node per transcript entry across frames, so DIR.Lib's measure memo and scroll cull lay out only the rows in or
near the viewport. A frame's cost no longer grows with the watchtower's transcript.

All numbers are CPU-rendered (`RgbaImageRenderer`), Release, on the machine the flight ran on; the first two frames warm the font and the
caches, and a frame is the best of nine after that (the test `ChatFrameTests.AFrameOfTwoThousandEntriesIsWithinTheBudgetAndNearOneOfTwenty`).

## 20 vs 2,000 entries at 1600x900

| | 20 entries | 2,000 entries |
|---|---|---|
| Before this flight (row cache off, 3 runs) | 8.2 to 8.8 ms | 129 to 164 ms |
| After, measured by the row-cache leg | 9.2 ms | 4.7 ms |
| After, three runs of the follow-and-captures leg | 7.8, 8.4, 8.0 ms | 9.3, 9.0, 8.7 ms |

The "before" runs are the row-cache leg's own, taken with the row cache switched off by a temporary switch that is gone from the branch
(commits 9d3da424 and 6bb4b972 add and revert it). The 2,000-entry frame went from about 16 times the 20-entry one to the same as it, within
the run-to-run noise of a few milliseconds. The test asserts under 250 ms and within 4x the 20-entry frame plus 25 ms.

## The chat in PageFrameTimeTests (the "many" and "long" states)

The two states give the chat a 2,000-entry transcript (`PageData.WatchtowerTranscript`; it was 60 and 10), with the reply in progress, two queued
lines and the timings under the box. Run as
`PILOT_FRAME_TIMES=<file> dotnet test Pilot.Gui.Tests -c Release --filter "FullyQualifiedName~PageFrameTimeTests&DisplayName~chat"`.
"Setup+first frame" builds the page and its rows (all 2,000 are built once, on the first frame); "arrange" is three arranges of the page's
tree, the first one filling the measure memo.

```
chat | many | 1400 wide | DPI 1	setup+first frame 560.3 ms	arrange 81.2 / 5.9 / 6.3 ms
chat | many | 500 wide | DPI 1.5 | 500 high	setup+first frame 132.5 ms	arrange 27.4 / 10.4 / 0.6 ms
chat | long | 1400 wide | DPI 1	setup+first frame 361.4 ms	arrange 216.6 / 2.4 / 1.1 ms
chat | long | 500 wide | DPI 1.5 | 500 high	setup+first frame 383.5 ms	arrange 337.0 / 9.4 / 1.0 ms
```

The first arrange of a state pays for laying out the rows in view and the memo; after it an arrange of the 2,000-row page is 0.6 to 10 ms.
Every case passes the test's 1000 ms ceiling by a wide margin, and the text-over-text sweep (`PageTextOverlapTests`) runs on the same
transcript.

## Captures

`docs/captures/chat-long-narrow.png` (760x640) and `chat-long-wide.png` (1600x900) follow the end of a 2,000-entry transcript with the
streaming bubble, two queued lines and the box; `chat-long-up-narrow.png` and `chat-long-up-wide.png` are the same page scrolled up, with the
Jump to end pill. They come from `ChatFrameTests.ALongTranscriptIsCapturedAtANarrowAndAWideWindow` with `PILOT_CHAT_CAPTURES` set to
`docs/captures`.
