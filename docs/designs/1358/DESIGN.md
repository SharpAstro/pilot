# Desktop cat buddy

**Objective:** A small cat in an always-on-top, transparent SDL window that sleeps in a corner of the desktop and wakes only when a watchtower session needs you or has good news.

## Draft cats

**Objective:** The three comic cats that were drafted, with Grump marked as the owner's pick; the others stay for reference.

```screen id=cats size=Laptop
Mochi, Grump (picked) and Noodle
```

**Notes:** Reached from Settings, "Other cats". Grump's moods, one per screen below: asleep (closed lines, flat mouth), one eye open (one half-lidded side-eye), alert (wide eyes, little "o" mouth, a yellow "!"), grudging happy (blush, tiny smile, "…fine."), side-eye (both lids half down, a "?"). Each cat is only filled, outlined rounded rectangles (about 15 shapes), so it maps one-to-one onto plain draw calls: no PNG/SVG asset and no animation format needed. Animation is tweening those shapes per frame: the body grows 2 px and back to breathe, Mochi's snot bubble swells and pops, Grump's lids twitch open, Noodle's tongue and tail sway. An animated PNG or SVG sheet stays an option if a hand-drawn cat is wanted later. The chosen cat then gets an awake, alert and happy pose for the other screens. Click a cat's name to see the buddy in its corner.

## Asleep

**Objective:** The cat's normal state: curled up asleep on its cushion, saying nothing while every session is fine.

```screen id=home size=Laptop
Asleep in the bottom-right corner
```

**Notes:** The window is transparent: only the cat, its cushion and the zZz are drawn, over whatever is on the desktop (shown here as dark blue). Slow breathing animation. Clicks pass through to the window below while it sleeps (a setting). In the prototype: click the cat to peek, the cushion for settings, the zZz for hover.

## One eye open

**Objective:** Hovering the sleeping cat opens one eye and shows the one-line watchtower summary, without waking it.

```screen id=asleep size=Laptop
Hover: the summary in a small bubble
```

**Notes:** Moving away closes the eye again. In the prototype, click the cat to play an ask arriving.

## A session needs you

**Objective:** The cat wakes, sits up and shows one speech bubble with who asks, what they ask, and a way to open it or put it off.

```screen id=attention size=Laptop
A designer session is waiting on the owner
```

**Notes:** One bubble at a time; more asks queue behind it and the peek lists them. "Later" puts the cat back to sleep for that ask only. Amber border only, the text carries the meaning.

## Good news

**Objective:** A short celebration when a PR merges or a flight lands, then the cat goes back to sleep by itself.

```screen id=good-news size=Laptop
PR merged: a stretch and a bubble for 10 seconds
```

**Notes:** Nothing to click; it fades out. In the prototype, click Grump to play a failure.

## Something broke

**Objective:** When CI fails or a session gets stuck, Grump wakes up furious and shows what broke and where to look.

```screen id=trouble size=Laptop
CI failed and a leg is stuck: angry Grump, red bubble
```

**Notes:** Ears flat, eyelids low, mouth open in a hiss with two fangs, a red anger mark on the forehead, tail puffed up, and a "#@!". The bubble uses a red border and an error icon, and the title says what failed. "Open the run" opens the CI run or the stuck session; "Later" puts it back to sleep for this failure only. Grump stays grumpy-faced (lids half down) until the failure clears, even while asleep.

## Peek

**Objective:** Clicking the cat shows every watchtower session at a glance: which node, what it is doing, and which one wants you.

```screen id=peek size=Laptop
The session list, the one asking first
```

**Notes:** Sessions that ask come first, then working, then idle. Clicking a row opens it in the GUI. Click the cat again or anywhere else to close.

## Settings

**Objective:** Right-clicking the cat sets what it watches, what wakes it, where it sits and how to snooze it.

```screen id=settings size=Laptop
The right-click settings card
```

**Notes:** "This node only" reads the local node directly instead of the watchtower session. The cat can also be dragged to any spot; the corner is where it returns.
