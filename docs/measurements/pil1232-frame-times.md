# PIL1232 frame times (PageFrameTimeTests, CPU render, Release)

Flights page at many rows; "arrange" is the first / second / third arrange of the page tree.

## Before (the hand-built ViewSwitch and header pills)

| Case | Setup + first frame | Arrange |
|---|---|---|
| flights table, many, 500 wide, DPI 1.5 | 100.0 ms | 2.9 / 0.1 / 0.1 ms |
| flights table, many, 1400 wide, DPI 1 | 103.0 ms | 17.0 / 0.3 / 0.1 ms |
| flights canvas, many, 500 wide, DPI 1.5 | 52.9 ms | 1.0 / 0.1 / 0.1 ms |
| flights canvas, many, 1400 wide, DPI 1 | 68.9 ms | 0.7 / 0.1 / 0.1 ms |
