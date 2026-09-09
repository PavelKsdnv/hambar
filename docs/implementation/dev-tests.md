## Dev smoke tests (`scenes/dev/`, `src/dev/`)

Headless end-to-end checks, each printing `PASS`/`FAIL` lines and exiting 0/1.
**Run all of them** — the older ones are the regression net for the newer ones.

`scripts/verify.py` runs them; what each asserts is in the test file. What is
*not*, and costs an afternoon:

- **A warning fails the run.** `verify.py` reds any logged `WARNING`/`ERROR`,
  since a scene quits 0 over a failed load. So a `PushWarning` must be narrow
  enough never to fire in a healthy run.
- **The list is discovered, not written.** `verify.py` globs
  `scenes/dev/*SmokeTest.tscn`, so a new test is live in CI and in the milestone
  skill the moment its scene exists. Naming tests in three places is what let
  `WorkerSmokeTest` ship without CI ever running it.
- **Synthetic input needs the right door.** `Input.ActionPress` works for held
  actions, but event-driven ones only reach `_UnhandledInput` via
  `Input.ParseInputEvent(InputEventAction)`.
- **A tool re-hovers from the real cursor every frame.** A headless driver must
  call `HoverAt` in the same frame as the assertion that depends on it, or
  `SetProcess(false)` to pin the hover.
- **`GhostColor` reads the tints the tool handed the mesh, not the mesh**: the
  headless dummy renderer keeps none, and `GetInstanceColor` answers black.
- **Wait on sim ticks, not frame counts**: assert after *N ticks*, and let
  `Simulation.Step` run them outright when real time is only in the way.
- **One `Main.tscn` at a time.** Two live instances put two nodes in the
  `simulation` group and `Simulation.For` answers whichever it finds first, so
  the second world wires itself to the first world's clock and streams. Free
  one and wait a frame before instancing the next.
- **Cells are searched, never hard-coded.** The tests find the nearest rock, a
  clear soil run, a run ending in water, free soil beside a road, and so on, at
  the point of use — so a seed change cannot quietly turn an assertion into a
  test of something else. **Reuse those helpers** rather than writing literal
  cells into new assertions.
- **Watch a hold, not the buffer at either end.** `AutomationLoopSmokeTest`
  first asserted the chain by sampling the field's buffer and the silo once a
  tick, and read zero from both while the run demonstrably earned money: a
  truck parked on a source it is blocked on loads in the *same tick* that puts
  grain there, so the buffer is empty again before the sample. A cargo hold
  stays full for the whole drive across, which is what makes it observable.
  The general rule for these tests: assert on the state that persists between
  ticks, never on the instant a transfer happens.

### Canonical views (`scenes/dev/ScreenshotTest.tscn`)

PNGs of the canonical views, so a human — or an agent — can look at what the
game actually draws.

**Must run windowed**: `--headless` gives a dummy rasterizer with no
framebuffer, so `FramePostDraw` never fires. `_Ready` detects it and quits 1.

**A view earns its place by showing something no other view can** — a ghost
verdict, a paused clock, six crop stages abutting at three zooms. Each `PASS`
line captions what the frame is meant to show; the caption, not the pixel
colour, is what says which cell killed a drag. It keeps the conventions above,
plus one of its own: views settle by **time**, not frame count, because the rig
smooths on `delta`.
