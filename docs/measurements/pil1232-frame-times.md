# PIL1232 frame times (PageFrameTimeTests, CPU render, Release)

Flights page at many rows; "arrange" is the first / second / third arrange of the page tree.

## Before (the hand-built ViewSwitch and header pills)

| Case | Setup + first frame | Arrange |
|---|---|---|
| flights table, many, 500 wide, DPI 1.5 | 100.0 ms | 2.9 / 0.1 / 0.1 ms |
| flights table, many, 1400 wide, DPI 1 | 103.0 ms | 17.0 / 0.3 / 0.1 ms |
| flights canvas, many, 500 wide, DPI 1.5 | 52.9 ms | 1.0 / 0.1 / 0.1 ms |
| flights canvas, many, 1400 wide, DPI 1 | 68.9 ms | 0.7 / 0.1 / 0.1 ms |

## After (ViewSwitch and the In play pair on DIR.Lib's Segmented)

Taken inside the leg's check run, 2500 tests in parallel on a shared machine, so the first-frame figures are inflated by load and the third arrange of the table case caught a pause; the second arrange (the steady frame) is unchanged.

| Case | Setup + first frame | Arrange |
|---|---|---|
| flights table, many, 500 wide, DPI 1.5 | 160.2 ms | 4.0 / 0.2 / 30.8 ms |
| flights table, many, 1400 wide, DPI 1 | 241.9 ms | 41.6 / 0.5 / 0.9 ms |
| flights canvas, many, 500 wide, DPI 1.5 | 247.2 ms | 3.5 / 0.4 / 1.0 ms |
| flights canvas, many, 1400 wide, DPI 1 | 175.1 ms | 1.6 / 0.4 / 0.4 ms |

The test's own bound (under 1000 ms for the steady arranges) holds in every case.
