## Camera (`src/camera/CameraRig.cs`)

**Structure.** The rig yaws; the camera under it is fixed at true-isometric
pitch (35.264° = atan(1/√2)), at 45° so 90° steps keep the iso diamond. **The
three things to know before changing it:**

- Zoom changes the camera's orthographic `Size`, not its position, and keyboard
  pan speed scales with zoom so screen-space speed feels constant.
- Drag pan converts pixels to world units via `Size / viewport height`,
  dividing the vertical component by `sin(pitch)` to undo the iso
  foreshortening — that division is what makes the ground stick to the cursor.
- Zoom and rotation smooth with frame-rate-independent decay (`1 -
  exp(-k·dt)`), over *unwrapped* target yaw, so repeated rotations accumulate
  rather than fight.

## Dev shortcuts (`src/ui/DevShortcuts.cs`)

What is left of the old number-key menu, none of it a build tool: the keys sit
at the *top* of the number row because the palette claims it from the bottom
up. Key 7 is the hand M5 replaces — it runs whatever the field under the cursor
is waiting for, through the door an order will take.

## Hover readout (`src/ui/CellInspector.cs`)

The debug instrument that confirms the generated world is what the generator
thinks it is. It names the owning `Field`/`Structure` — the entity, not the
cell — and formats with `InvariantCulture`, so the text reads the same
everywhere. **Off the map needs no bounds check**: the terrain layer answers
`OutOfBounds`. Not the player-facing panel; that is `## Field panel`, and M6's.

**Trap: a building's contents are not in this readout.** `#37`'s silo makes
`Structure.Storage` worth showing, but `BuildSmokeTest` pins the exact text
`tile: structure (Name)`; appending a contents summary after the name breaks
that assertion (`Contains` needs the closing paren right after `Name`). Left
for the building panel M6 owes structures, the way `## Field panel` already
reports a field's buffer rather than this one.

## Field panel (`src/ui/FieldInspector.cs`)

> **It reports facts about the field and never points at the culprit.** A
> stalled crop reads "stalled": no season named, no factor blamed, no advice.
> M6's building panels inherit that rule under a much sharper version of it,
> and a habit the cheapest panel in the game breaks is not one.

**Days remaining are the growth model's live rate** (`GrowthPerTick`), never
the nominal schedule — a countdown is read exactly when the ground or the
season is why the schedule is wrong. **A zero rate is a word, not a division**:
dividing by one is how a panel promises a harvest in eight thousand days, so
`Stalled` is settled first, and the stages time does not move get no countdown.

**Yield is `ProjectedYield` itself, never a second formula** over the same
inputs ([`## Crops`](crops.md)), so what is shown and what lands cannot drift.
The buffer line is there for the same reason: a full buffer refuses the next
harvest whole, and nothing else shows that coming. **A view, and only a view**,
repainted every frame like the time bar, so a countdown runs down and a
bulldozed field closes the panel unprompted. Selection is
[`## Cell picking`](world-grid.md); an **armed build tool owns the click**.
**Deferred:** renaming a field, and every building panel — M6's.
