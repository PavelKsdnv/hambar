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

## Vehicle panel (`src/ui/VehicleInspector.cs`)

> **The blocked reason is the headline, not a footnote.** #7's tracker names
> the failure this panel exists to prevent — the player cannot tell why an
> idle vehicle is idle — so that row is always on screen, reading an em dash
> for "nothing to report" rather than disappearing, the same convention the
> field panel uses for its own empty fields.

Follows the field panel's shape exactly (a view over `WorldGrid`/`Fleet`,
selection through `CellPicker`, an armed build tool owns the click) mirrored to
the **right edge, vertically centred**. `MachineSystem.SetOrder` already does
the validating and the blocking (`## Orders`, orders.md); this panel only ever
reads `OrderOf`/`StepOf`/`BlockOf` through `OrderText` and adds the one thing
that file has no opinion on: which target *kind* an action needs.

**Legality at pick time is a type match, nothing finer** — a field order
accepts any field, a haul accepts any building, exactly as far as `Order`'s
own "existence is checked at execution, not at assignment" already goes.
Whether that field is at the right stage is `OrderBlock`'s job once the
vehicle arrives, never this panel's to pre-judge; picking a just-ploughed field
for another plough is accepted here and reported `WrongStage` a few ticks
later, which is correct, not a bug.

**A haul is two picks, not one** — source, then destination — held as one
`int?` field until the second lands; the good is never asked for, since grain
is the only real row `ItemTypes` has today and a picker for a catalogue of one
would be content nothing can consume. The day a second good exists, this is
where it gets a control.

**Two panels, one click, no reference to each other.** `FieldInspector` and
this class each read a public fact of the other's instead: a group
(`VehicleInspector.PickingGroup`, joined only while a target is being picked)
and a static lookup (`VehicleInspector.VehicleAt`, the same
`MachineSystem.Occupancy` query this panel's own selection uses). Either one
being true means "this click is not the field panel's" — checked before it
claims one, never the other way around, because whichever panel runs its
`_UnhandledInput` first in a frame is not this codebase's to pin down.
`AnyToolArmed` is the same arm's-length idiom already established for the
build tools, copied rather than shared, since the two panels otherwise hold no
reference to each other at all.

**Trap: `SetOrder` queues, it does not apply.** A pick lands in the same
queue-of-one `## Orders` describes, so the order it names is `PendingOrderOf`
for the rest of that tick and `OrderOf` only once a tick has actually run —
the smoke test asserts the former immediately after a pick and the latter only
after stepping, and a screenshot view that skips the step shows a panel
reporting "idle" and "none" over an order that was, in fact, accepted.

**Trap: an existing field or building has real height** (`assets/dev/tile_library.tres`:
up to 1.0 for a harvestable field, 1.8 for a silo), which is exactly what
`BuildTool.HighlightY` never has to clear — its ghost only ever previews a
placement onto bare terrain. Highlighting an *already-placed* target at that
height buries the quad under the target's own mesh; this panel's highlight
sits at its own, taller `FieldHighlightY`/`StructureHighlightY` instead, found
by a screenshot showing a picking panel with nothing lit on the map.

**The 3D highlight is parented to `World`, never to this `Control`.** A
`MeshInstance3D` added under a `Control` that lives inside the `Hud`
`CanvasLayer` never reaches the 3D scene the build tools' own ghosts render
into — found the same way, a screenshot with the right panel state and no
highlight to show for it. **Deferred:** naming a worker (the pool has none to
give — `## Labour and wages`, labour.md); a "next" row for a queued re-point,
left out because `PendingOrderOf` cannot itself tell "nothing queued" apart
from "queued to go idle", so a HUD built on it would be showing a guess.
