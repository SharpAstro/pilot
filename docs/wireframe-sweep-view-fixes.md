# Wireframe sweep: the view fixes (PIL1053, leg view-fixes)

The sweep (`WireframeSweepTests`, DIR.Lib 11.60 `Wireframe.Check`) drew 16 pages at 12 sizes (the "many" state at 8), 320 cases.
The known-findings list that leg wireframe-sweep left is gone (`WireframeKnownFindings` and its stale-entry test are deleted):
every finding is now fixed in a view or sits in a commented exemption.

## The findings and what fixed each

| Page | Finding | Fix |
|---|---|---|
| chat, transcript | A failed tool call's output line (a long report) is one text leaf, cut by the column's clip with no ellipsis, and it lies partly over the scroll bar's region (CutText, TextOverRegion) | `TranscriptEntryView.Mono` and the Waiting and chat Sent/Failed lines go through `WrappedProse.OneLine`: a line of 80 characters or fewer stays one leaf (a command stays whole for a copy and for its tests); a longer one is words in a wrap capped at one line, which is the only wrap DIR.Lib ends in an ellipsis. Whole text on hover |
| jobs | At 1000 wide (1.5 scale) the first divider's grip overlaps the Command header button, and the "Command" title lies over it (RegionOverRegion, TextOverRegion) | `ListTable.Fit`: a squeezed table leaves out the columns a narrow page drops, instead of letting the engine collapse them. A collapsed column's weight left the header's star shares but not the divider row's, so a star column was 3.5 units wider under the dividers than in the header |
| jobs | At 1400 wide a cut cell shrinks to a bare ellipsis under the row's own hit area | The Command column keeps 100 (squeezed 70) in place of 0 |
| jobs | At 1000 and 1280 wide (1.5 scale) the detail panel's scroll regions reach 36 px past the window's foot | The table's minimum height is 110 (was 140): the panel and table share the rest 3 to 1 and the table's share was clamped up, pushing the panel out |
| pull requests | At 1000 wide the search box is narrower than its "Search repos" placeholder, which lies partly over the box's text-input region | `SearchFloor` 60 to 110 |
| flights canvas | The LAYERS palette grip panel overlaps the continent header region `map:` (RegionOverRegion) | Exemption (below) |
| flights canvas | Flight, leg and state labels lie half under the zoom pill or the LAYERS panel (TextOverRegion) | Exemption (below) |

## Exemptions (in `WireframeSweepTests.OptionsFor`, no rule switched off)

- `IgnoreRegion`: a `ButtonHit` whose action starts `map:` and is not `map:zoom`. These are the regions the flight's data puts down (a continent header,
  a row, a destination, a leg, a plane). They lie where the flights are, under the overlays by design. The zoom pill, the LAYERS palette and every other
  control are still judged.
- `IgnoreText`, on the flights canvas only: a text that lies under the zoom pill or the LAYERS panel, which are painted over the map after it. These are
  the map's labels, which sit where their flight is while the pan puts the overlays over whatever is there. The overlays' own text and every other page's
  text stay judged.

## The sweep's time

`dotnet test --filter FullyQualifiedName~WireframeSweep` runs 320 cases in 26 to 29 s (test host time, two threads), under the 30 s the class has.
The whole `Pilot.Gui.Tests` in Release with `GITHUB_ACTIONS=true`: 3779 passed, 0 failed, 3 m 43 s.

## The revert proof (scratch copy under the job tmp folder, not committed, removed)

The issue's PIL1027 was pilot-tui's help overlay, which Console.Lib paints, so the sweep cannot see it. A GUI label fix stands in for it.

1. Sessions column and table squeeze reverted (the Session column's `SessionMin` and squeezed width of #81 removed, and `ListTable.Fit` no longer squeezing):
   20 cases failed, 78 findings on the Sessions page alone (at 1000 wide, 1.5 scale, "long" and "many"), for example
   `text "Context" is drawn over text "Session"` and `text "1k / 200k · 1%" is drawn over text "pilot · PIL381 / overlap-check-with-a-leg-name-that-is-long"`.
   The fleet, flights table, jobs and repos pages failed too, since they share `ListTable.Fit`.
2. A label fix reverted (`SearchFloor` back to 60 on the Pull requests page): 2 cases failed,
   `pull requests | long | 1000 wide | DPI 1.5 | 900 high` and the "many" case at the same size,
   `text "Search repos" lies partly over the control TextInputHit`.
3. Not caught: PIL770's range-row captions (#887). Forcing `RangeCaptions.Span` to a whole line, and then to one column, left the sweep green. The Settings
   sample's range rows are not on screen in the sizes drawn, or their captions sit where the sweep does not look. So PIL770 is not proven by the sweep, and
   `RangeCaptions`' own tests remain its guard.

## Go-around 1: squeezed rows keep their own cells

`ListTable.Fit` leaves out the columns a narrow page drops, but rows still passed a cell per declared column, so cells shifted under the
wrong titles (Jobs' For cell under State; Sessions' Model cell under State). Each fitted column now carries its declared `Slot`, and
`ListTable.Row` keeps the cell of each column that stayed. `SqueezedTableCellsTests` asserts Jobs' `running` and a Sessions state word
sit under `State` at 1000 wide, scale 1.5. The wireframe check cannot see this (nothing overlaps).
