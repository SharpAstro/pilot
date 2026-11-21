# PIL1232 frame times (CPU render, Release)

The fleet pages' segmented rows (ViewSwitch, In play / Everything) are not converted: DIR.Lib's Segmented has no cell form yet (SharpAstro/DIR.Lib#299), and the terminal draws the same trees, so they keep their button groups for now. No Flights numbers are recorded.

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
