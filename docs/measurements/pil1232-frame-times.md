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

## Designs page (frame toolbar size row on DIR.Lib's Segmented)

Converted control: the Designs frame toolbar size row (Desktop, Laptop, Tablet, Phone, Terminal), one-of-few, now `Layout.Builder.Segmented`. Rotate and Point at this stay buttons. Sample design DSN-1 with two frames, unloaded run (own test, `DesignToolbarCaptureTests`).

### Before (plain buttons)

| Case | Setup + first frame | Arrange |
|---|---|---|
| designs page, standard, 700 wide | 491.1 ms | 1.5 / 0.7 / 0.7 ms |
| designs page, standard, 1920 wide | 101.5 ms | 1.4 / 1.9 / 2.4 ms |
| designs page, night, 700 wide | 24.3 ms | 1.0 / 0.8 / 1.0 ms |
| designs page, night, 1920 wide | 17.4 ms | 0.9 / 1.0 / 1.0 ms |

### After (Segmented size row)

| Case | Setup + first frame | Arrange |
|---|---|---|
| designs page, standard, 700 wide | 369.1 ms | 2.0 / 1.1 / 1.0 ms |
| designs page, standard, 1920 wide | 58.5 ms | 0.9 / 0.7 / 0.7 ms |
| designs page, night, 700 wide | 20.6 ms | 1.0 / 0.8 / 0.8 ms |
| designs page, night, 1920 wide | 18.4 ms | 0.9 / 1.0 / 0.8 ms |

The first frame of the first case includes one-time font and renderer warm-up. Steady arranges are about 1 ms before and after; the test bounds them under 1000 ms.
