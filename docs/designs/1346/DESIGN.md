# Notifications: real toasts, view modes

**Objective:** The Notifications page draws DIR.Lib's real toast and gets a Grid/List mode switch like Flights and Ideas, using the whole width with readable text.

## Grid

**Objective:** Real toasts (tint, severity icon, close, round corners) in a grid that fills the width; open ones on top, closed history below.

```screen id=grid size=Desktop
Grid mode, 1600 wide
```

**Notes:** Detail is folded behind "Detail"; duplicates fold into one toast with ×2. Closed toasts lose their buttons and close icon.

## List

**Objective:** The same toasts as full-width rows with larger text; open rows carry their actions on the right, closed rows are one quiet line each.

```screen id=home size=Desktop
List mode, the stray toast's detail unfolded once
```

**Notes:** Screen id `home` is the List mode (the starter file could not be removed). The detail repeats nothing from the summary. In the terminal the page is List only (one row per toast, severity glyph, the actions as keys); Grid falls back to List there.
