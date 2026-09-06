# Implementation notes

What exists in the codebase today and how it fits together. Companion to
`tech.md` (the engine decisions) — this documents how those were realized, and
the design decisions taken since.

**How to read this file.** It is organized by subsystem and every section is
written to be read *alone*. Find the subsystem you are touching in the index,
read that section, and stop:

```bash
sed -n '/^## World grid/,/^#/p' docs/implementation.md
```

**What is written here and what is not.** Inventory — member lists, tile art,
what each test asserts — is deliberately thin, because the source says it better
and the source cannot go stale. What is written at length is the part no amount
of reading `src/` recovers: why a thing is shaped the way it is, what was
rejected and why, and what is deferred on purpose. Keep it that way when you add
to it. A section that has grown into a prose transcript of its own source file
should be **cut back, not extended** — see the budget in
`.claude/skills/milestone/SKILL.md`.

Last updated: 2026-09-05.

## Index

| Section | Read it when |
|---|---|
| `## Building & running` | building, running or adding a scene |
| `## Project layout` | looking for where something lives |
| `## Simulation` | touching the tick, sim state, or anything the view draws |
| `### Entity storage` | adding an entity kind, or asking what is near a cell |
| `## Game calendar` | touching days, seasons, game speed or pause |
| `## Camera` | touching `CameraRig`, projection or input |
| `## World grid` | touching world state, terrain, tiles or road queries |
| `## Machines` | touching vehicles or movement |
| `## Build palette` | touching the toolbar or tool selection |
| `## Dev shortcuts` | adding a dev key |
| `## Build tools` | adding or changing a placement tool, or a placement rule |
| `### Fields` | touching farmland |
| `### Structures` | touching buildings |
| `### Bulldozing` | touching removal or refunds |
| `### Money and build costs` | touching prices, the balance or affordability |
| `## Cell picking` | mapping screen to cell |
| `## Hover readout` | touching the debug readout |
| `## Dev smoke tests` | writing or fixing a test — **read before writing one** |
| `## Not yet implemented` | before assuming something is missing by accident |

## Building & running

- **Godot 4.7 stable (.NET/mono build)**, C# on **.NET SDK 8**. Forward Plus on
  D3D12, physics Jolt (both in `project.godot`).
- `dotnet build Arable.sln` — the `.csproj`/`.sln` are hand-written because
  Godot's `--build-solutions` will not create them from scratch.
- Run with `godot --path .`. Main scene is `scenes/Main.tscn`.

## Project layout

```text
assets/            dev/ art incl. the GridMap MeshLibrary; icons/toolbar/ the
                   build-tool icons (white SVGs, tinted per state)
docs/              design docs (concept, tech decisions, this file)
scenes/            Main.tscn is the entry point; world/ holds instanced
                   scenes; dev/ holds test scenes, not part of the game
src/sim/           the fixed tick, the calendar, sim/view contracts, entities
src/camera/        the camera rig
src/ui/            HUD, input handling and picking; build/ holds the tools
src/world/         world state, terrain, placement rules and entities
src/dev/           the scripts behind scenes/dev
```

Namespace is `Arable` throughout. `.godot/` is generated cache — never edited or
committed.

**Gotcha (hand-written .tscn):** a Node-typed export serialized as
`World = NodePath("../World")` only resolves if the `[node]` header also carries
`node_paths=PackedStringArray("World")`. Without that marker the property loads
as null.

## Simulation (`src/sim/`)

> **The view reads sim state; it never writes it.** The same rule `WorldGrid`
> holds over its `GridMap`, one level up and now enforced by types:
> `ISimSystem.Tick` is the only place sim state may change, `ISimView.Interpolate`
> is handed an alpha and poses visuals from state it must treat as read-only. A
> node that is still both (a `Machine`) implements both halves and keeps them in
> separate methods.

`SimClock` is the accumulator — real seconds in, whole ticks out, leftover
fraction kept as `Alpha`; `Simulation` is the `Node` that drives it and holds
the registrations. Why it is shaped this way:

- **20 Hz, and deliberately not 60.** Matching Godot's physics rate would let a
  coupling bug hide behind the coincidence; at 20 Hz anything accidentally
  running per physics step is off by 3×. It is an export, not a constant, so the
  cost of the rate stays measurable.
- **Fed from `_Process`, not `_PhysicsProcess`**, which is already a fixed step
  owned by the frame loop: clocking off it would make the sim a divisor of the
  render rate, exactly the coupling this removes.
- **Overrun is dropped, not owed.** Past `MaxTicksPerFrame` (5 = 0.25 s of sim)
  the surplus is discarded and counted in `DroppedTicks`; a backlog would make
  the next frame slower still, which is the spiral of death. So a sim that
  cannot keep up runs **slow**, never at a variable step: determinism survives
  and only the tie to the wall clock is lost. `DroppedTicks > 0` is the signal.
- **The view lags one tick.** The blend ends at the *current* state, so the
  picture is up to 50 ms behind. Extrapolating instead would mispredict corners.
- **`Alpha` can be exactly 1.** Deltas summing to a whole tick in real
  arithmetic can fall a hair short in doubles, and the remainder rounds to 1 in
  single precision. Clamped and harmless — but for the same reason, measure sim
  time as *ticks + alpha*, never as whole ticks alone.
- **Found by group, not `NodePath`**: entities are instanced at runtime, so an
  exported path cannot reach them. The node ticks at `ProcessPriority = -100`,
  so a transform read this frame is the pose the sim just produced.
- **Deferred:** RNG streams (#24) and determinism hashing (#25). Pause, time
  scale and the calendar landed with #23 — see `## Game calendar`. Registration
  during a tick is unsupported — an entity that wants to stop goes idle inside
  its own `Tick`.

### Entity storage (`src/sim/EntityStore.cs`, `EntityId.cs`, `SpatialHash.cs`)

Sim entities are rows in flat arrays, not nodes. What forces it is not frame
cost but **walkability**: the determinism hash (#25) and save/load (M10) both
have to visit all sim state cheaply in a fixed order, and a scene subtree gives
an order that depends on spawn history plus an allocation per entity.

- **`EntityStore` owns liveness and nothing else.** It hands out and recycles
  slots; each system keeps its own parallel arrays indexed by `EntityId.Index`.
  No component registry, no archetypes, no queries — tech.md asks for a
  data-oriented *layout*, not an ECS framework, and M4/M5 are the milestones
  that will say what components exist. Generalising before then is guessing.
- **The generation is the point of the handle.** Slots are reused, so an index
  alone cannot tell "the machine I spawned" from whatever moved into its slot
  after it died; that bug reads as teleportation. `Destroy` bumps the slot's
  generation, invalidating every stale handle at once, and live generations
  start at 1 so `default(EntityId)` is dead though slot 0 is real. A recycled
  slot still holds the old values, so a spawn must write **every** column.
- **Walk by slot, never by dictionary.** `SlotCount` + `IsAliveSlot` is the only
  iteration order offered: ascending, independent of creation order. Enumerating
  a `Dictionary` is the quiet way to lose determinism.
- **`SpatialHash` is a bucket of ids per grid cell** — keyed by cell because the
  world is already cell-addressed and `WorldGrid` owns the rounding rule, so a
  second granularity would be a second rule to keep in step. It is a *derived*
  index: rebuildable, never saved, never hashed. Its queries walk an explicit
  ascending cell range rather than enumerating buckets, for the reason above,
  and emptied buckets are dropped or a roaming mover grows it to map size.

## Game calendar (`src/sim/GameCalendar.cs`, `src/ui/TimeControls.cs`)

> **Two clocks, and conflating them is the trap.** `SimClock` is the *tick
> scheduler* — real seconds in, whole ticks out. `GameCalendar` is the *date* —
> ticks in, days and seasons out. Neither measures what the other does, which is
> exactly what lets the speed control move the first and never the second.

**A day is a count of ticks, not a span of seconds**, which makes "the same
number of ticks elapses per simulated day at every speed" true by construction
rather than by arithmetic somebody keeps honest: 3× runs the ticks sooner and
the day still ends on tick 600.

**The numbers, and why.** Day = **600 ticks** (30 s at 20 Hz), season = **12
days**, year = 48 days ≈ 24 min at 1×. Thirty seconds is about how long a
machine takes to cross the 97-cell map, so a day is roughly one haul end to end
— a unit the player already feels — and short enough that a five-minute playtest
sees ten of them, which is what makes day-scale effects (M7's price drift, M8's
deadlines) observable in a sitting. 600 factorises hard (2³·3·5²) and 12 divides
by 2, 3, 4 and 6, so sub-day schedules and M4's whole-day growth stages both
land without remainder. Both are `[Export]`s on `Simulation` — M4 calls crop
cadence the tempo of the entire game, so expect them to move. The season *count*
stays at four: which seasons exist is content, their length is the tempo knob.

**The whole of the calendar's state is one `long`**; everything else is
division, because #25 must hash sim state and M10 must save it and an integer is
the cheapest thing either can walk. Day and year read **1-based** while
`TotalDays` is 0-based — mixing them is off-by-one in every printed date. And
there is deliberately **no rollover signal**: a system that cares keeps the day
it last acted on in its own arrays, which saves and hashes with the rest of its
state, where an event would have to be re-fired by a load, replay or date jump.

**Speed scales the schedule, never the step.** `SimClock.Speed` multiplies the
real time booked, so 2× runs twice the ticks per real second with `TickDelta`
untouched; a variable step would make a run depend on the speed the player
happened to watch at, leaving nothing to hash or replay. Rejected:
`Engine.TimeScale` (scales the *frame* delta, so it would speed the camera
smoothing up with the sim) and `SceneTree.Paused` (stops the camera too — a
paused world still has to be lookable-around). `MaxTicksPerFrame` is deliberately
*not* scaled with it: it guards how much work one frame may do, a real-time
quantity, so at 3× it bites below ~12 fps and the sim runs slower than promised,
visibly via `DroppedTicks` — never by changing how many ticks make a day. And
**speed is not sim state**, so #25 should not hash it; the calendar is the
opposite and belongs at the top of that list.

**The controls copy the palette** (`## Build palette`) rather than inventing a
second idiom, and sit **bottom-right**, the last free corner, with the date in
the same panel as the buttons that move it. **Step 0 is always pause**: the
ladder is otherwise free-form, but the code must find the stop, so one not
starting at 0 is repaired at load with a warning. **Deferred:** a pause *key*
(the number row is the palette's) and season reactions — M4 growth, M9 hazards.

## Camera (`src/camera/CameraRig.cs`)

RTS-style isometric camera, per tech.md: orthographic, fixed pitch, pan + zoom +
90°-step rotation.

**Structure.** The `CameraRig` sits on the ground plane and only ever yaws; its
child `Camera3D` holds everything else fixed — true-isometric pitch (35.264° =
atan(1/√2)), orthographic projection, a distance offset. The rig starts at 45°
yaw, so 90° steps keep the classic iso diamond.

**Controls** (actions live in `project.godot`): WASD/arrows pan relative to yaw,
middle-drag pans 1:1, wheel zooms, Q/E rotate in 90° steps; tunables are
`[Export]`ed. **The three things to know before changing it:**

- Zoom changes the camera's orthographic `Size`, not its position, and keyboard
  pan speed scales with zoom so screen-space speed feels constant.
- Drag pan converts pixels to world units via `Size / viewport height` and
  divides the vertical component by `sin(pitch)` to undo the iso foreshortening.
  That division is what makes the ground stick to the cursor.
- Zoom and rotation smooth with frame-rate-independent exponential decay
  (`1 - exp(-k·dt)`), and the rig tracks *unwrapped* target yaw, so repeated
  rotations accumulate instead of fighting each other.

## World grid (`src/world/`)

Four entity kinds exist: **roads** (traversable tiles), **fields** and
**structures** (a tile layer *plus* an entity owning those cells — the tile says
only *that* something is placed), and **machines** (sim rows, deliberately *not*
grid cells).

**Data/view split.** `WorldGrid` owns the logical world state; its child
`GridMap` is presentation only, and game logic must **never read it back** —
every mutator keeps the view in sync. `## Simulation` states it for entities.

**Two layers, stored separately.** What the land *is* (`TerrainType` plus a
fertility float, generated from the seed) and what the player *built*
(`TileType`, sparse) never share storage — which is what makes bulldozing
lossless: clearing back to `Empty` leaves the terrain untouched. `GetTerrain`
answers `OutOfBounds` off the map rather than throwing, so a caller tells "not
on the map" from any real terrain in one call.

**Terrain generation** (`GenerateTerrain`, run before the start road):

- The map is **bounded**: `MapHalfExtent` 48 → 97×97 cells = 194 m at 2 m cells,
  which fits Main.tscn's 200×200 ground plane and is slightly larger than
  maximum zoom-out shows.
- **One seed** drives everything: a fertility noise and a rock/water mask
  (`WorldSeed + 7919`), both SimplexSmooth + FBM. The mask reads as a coarse
  elevation — low is water, high is rock, the rest soil — so water and rock
  deliberately never border each other. Defaults ≈ 76/14/10 soil/water/rock.
- Fertility is **0 on rock and water**: it means "how good is this soil".
- Storage is **flat arrays** indexed by cell, not a dictionary — every in-bounds
  cell has a value. The extent the arrays were built with is cached, so changing
  `MapHalfExtent` at runtime can never index past them.
- It is **deterministic and re-runnable**: same seed in, bit-exact same arrays
  out, and it never touches the placement layer.
- The starting road strip (z = 0, x = −16..16) is **carved** to soil rather than
  biasing the noise, so the seed still owns every other cell.

**How the two layers render.** One `GridMap` draws both — the placed tile's mesh
when a cell has one, the terrain's otherwise — so placement *hides* terrain
rather than overwriting it, and clearing brings the same terrain back. Cells are
2 m, origin on the ground plane; dev art gives soil **four fertility shades**,
so the fertility field is legible in the iso view.

**Road-network queries live here**, because they are questions about the grid
rather than about a vehicle:

- `FindRoadPath` — BFS over road cells, 8-neighbour, where a diagonal step is
  allowed only past a road *corner*; that rule is what stops a path squeezing
  through the point gap between two unconnected strips. `SmoothRoadPath` then
  string-pulls it, dropping every waypoint the vehicle can drive straight past,
  tested by an exact integer supercover walk under the same corner rule.
- `LineCells` — Bresenham, except a diagonal step stair-steps through two
  orthogonal cells so the footprint stays 4-connected, and the connector cell
  **alternates sides** on successive diagonals so the staircase stays centred on
  the true line. Without that, machines — which drive centre to centre — would
  hug one edge of the band.
- `RectCells` — the filled inclusive rectangle, row-major from the minimum, so
  the list depends on the rectangle and not on which corner the drag started from.

**Mutators.** `SetTile` is the single write path and maintains both entity
registries, so no caller has to remember to. `MarkField`, `PlaceStructure` and
`BuildRoadLine` are the **unvalidated** entry points, for start layout, dev keys
and tests; anything the *player* places goes through a `BuildTool` and therefore
`PlacementRules`. `Clear` is the one removal path, returning **null** — not an
empty `Removal` — when the cell held nothing.

Two asymmetries in `SetTile` that everything downstream inherits: writing over a
field cell detaches **just that cell**, and a field that loses its last cell is
dropped; writing over **any** cell of a structure demolishes the whole building.
`Clear` returning null is load-bearing for the second — it is what stops one
building being refunded once per cell a drag clipped.

**Start layout** (`GenerateStartRoad`): one straight road along x through the
origin, on the soil strip generation carved for it, and nothing else.

## Machines (`src/world/MachineSystem.cs`, `src/world/Machine.cs`)

A machine is any vehicle — tractor, combine, truck. **State and drawing are two
objects**: `MachineSystem` keeps every machine's position, yaw, route and parked
flag in parallel arrays (`### Entity storage`) and ticks all of them in one loop;
the `Machine` node is an `ISimView` owning the mesh and nothing else. `WorldGrid`
constructs the system and registers it with the `Simulation`, because machines
run on the road queries it already owns and there is exactly one world.

- **The node transform is a drawing, not a position.** `Interpolate` writes it
  each frame from the row's last two sim poses; `SimPosition` is where the
  machine is. The exports (`Speed`, `TurnSpeed`, `BodyColor`) are spawn *input*,
  read once off the instanced scene and copied into the arrays — changing one on
  a live node does nothing.
- **Dev model:** a box whose forward is **−Z**, meshes under a `Model` child
  carrying the visual scale, so resizing the art never touches movement code.
- **Behavior:** wander — random road cell, `FindRoadPath`, `SmoothRoadPath`,
  drive the waypoints, repeat. The smoothing is what keeps a stair-stepped
  diagonal road from being driven as a zigzag; one that strands off-road warns
  and parks. Each tick spends a travel budget (`Speed·dt`) across waypoints so
  corners lose no distance, and yaw smooths frame-rate-independently so changing
  the tick rate does not change how fast it looks like it turns. The previous
  pose is snapshotted for *every* live machine, parked included — a stale one
  makes the view drift.
- **Freeing the node despawns the row.** `Machine._ExitTree` is the one place a
  view touches sim state, and it is lifecycle rather than a state write (the
  exemption `Simulation.Register` has). Nothing else notices a freed node, and
  an orphan row is an invisible machine still driving the roads.
- **Determinism:** a seeded `System.Random` per machine plus a fixed-seed spawn
  counter. That `Random` is the last reference-typed column; #24 replaces it.

## Build palette (`src/ui/BuildPalette.cs`)

> **The toolbar is how a build tool is chosen**, and it is the pattern every
> other HUD bar copies (`## Game calendar` is the second): one button per entry,
> built in code from an exported list, reading its state off the thing it
> controls, with one selection path and a repaint every frame.

**Built in code from the exported `Tools` list**, in bar order, so the order is
a design decision rather than an accident of tree order — putting M5's silo on
the bar is adding it to that list. Each button reads its name, icon and price
off the tool, so the bar can never quote a number the click does not charge and
repricing in the editor moves the button. A tool with no icon falls back to its
name, so a half-wired scene degrades instead of showing a blank square; prices
are grouped with `InvariantCulture` so the bar and the balance agree everywhere.

**It is not a second source of truth.** Which tool is armed is a fact about the
tools (`BuildTool.Active`, kept unique by the `build_tools` group); `Refresh()`
repaints from that every frame, so a tool disarmed by *any* other route — Esc,
right click, another tool arming, a dev script, a screenshot run — is right on
the bar the same frame, with no palette-side copy that could disagree.

**Keys are an accelerator for the bar, not a way around it.** Key *n* calls the
same `Toggle` a button press calls; the palette claims `menu_1` upwards, one key
per entry, so the range grows with the roster. `Select` is public so a headless
test arms tools along the path a click takes, without synthesizing mouse events.

**The M8 seam is `ToolAvailability`** — `Available`, `Locked` (greyed, still on
the bar) or `Hidden` (off it). A non-available entry is refused on **every** path
in, because a seam only one path respects is decoration; and a tool that stops
being available while the player holds it is disarmed on the spot, so the bar
never says "locked" about the tool the clicks are going to. The *rules* are M8's:
nothing here knows what an unlock is.

**Two gotchas in the look.** Icons are tinted with a *modulate*, which
multiplies, so **the source artwork has to be white** — a dark glyph would stay
dark whatever colour it was given. And the root `Control` is
`MOUSE_FILTER_IGNORE`, as are the inner labels: a full-screen `Control` would
otherwise swallow every click meant for the world.

## Dev shortcuts (`src/ui/DevShortcuts.cs`)

What is left of the old number-key menu: **8** toggles the hover readout, **9**
spawns a machine. Neither is a build tool — which is why they still need a home
— and both sit at the top of the number row because the palette claims that row
from the bottom up. Nothing in this node reaches a build tool, which is the
point: after M2 there is one way to choose one, and it is the palette.

## Build tools (`src/ui/build/`, `src/world/PlacementRules.cs`)

Every mouse-driven placement tool sits on one base, `BuildTool`: hover
highlight, ghost preview, validation that refuses an illegal placement *before*
the click, and the anchor → preview → place → cancel interaction.
`RoadBuildTool` is 30 lines, most of them comment — copying it is how the next
tool gets written. A subclass supplies only what differs; fields and structures
override `Apply` to call `MarkField`/`PlaceStructure`, because the default would
write tiles and leave farmland or a building nothing could address.
`BulldozeTool` is the one subclass that bends the base — see **Bulldozing**.

**The rules** (`PlacementRule`, a `[Flags]` set — add a flag rather than
re-coding a check in a tool); each exists for its own reason:

- `BuildableTerrain` — soil only; rock, water and off-map are refused (`OffMap`
  is a separate refusal so the message can differ).
- `NoOverlap` — the cell must not hold a *different* placement. Placing what is
  already there stays legal, which is what lets a road branch off the network.
- `VacantCell` — **nothing at all** on the cell. Stricter than `NoOverlap` by
  exactly the "already there" case. Fields need it because a cell belongs to
  exactly one `Field`, so a second rectangle over the first would leave two
  entities claiming the same ground.
- `InBounds` — on the map and nothing else, the looser form of
  `BuildableTerrain`. It exists for the bulldozer, whose business the terrain
  kind is not: what may be *taken off* a cell has nothing to do with what could
  be built there.
- `OccupiedCell` — the exact inverse of `VacantCell`, refusing with
  `NothingToClear`. Mutually exclusive with it: a tool wants the cell free or
  wants it taken, never both.
- `TouchesRoad` — a **footprint-level** rule: some cell must share an edge with
  a road cell *outside* the footprint, so a placement can never satisfy its own
  road requirement. 4-neighbour — a road meeting only a corner is not access.
  It is what makes the road network load-bearing rather than decorative, and
  because it is footprint-level a refusal blames no individual cell.

`PlacementRules.Check` returns a `PlacementPlan` — per-cell refusals, the one
refusal describing the whole placement, and what committing it would cost.
How verdicts add up is the `FootprintPolicy`, and there are exactly two, because
building and clearing want opposite answers about a mixed region:

- `EveryCell` (every building tool): **partial legality is all-or-nothing** —
  one illegal cell refuses the whole drag, because a half road, or a field with
  a bite out of it, is not what the player asked for. The per-cell verdicts
  exist only so the ghost can point at the offenders.
- `AnyCell` (the bulldozer, and only it): one passing cell is enough and the
  failing ones are skipped. It is also what a placement is *priced* on — an
  `AnyCell` drag pays for the cells it acts on, not the ground it crossed.

**What the player sees.** A blinking hover square coloured by the verdict, and
once anchored a `MultiMesh` ghosting the footprint per cell — a refused drag
never shows a legal-coloured cell, because none of it will be built. The one
promise the ghost makes is **a legal-coloured cell is a cell something will
happen to**, which is why `GhostColorFor` is virtual: under `AnyCell` a legal
plan still contains cells it will skip, and the bulldozer dims those.

**Interaction.** The first left click anchors (and is itself validated), the
second places and re-arms. A refused click writes nothing *and keeps the
anchor*, so the player re-aims. Right click / Esc drops the anchor first and
leaves the tool second; `NeedsAnchor = false` skips the anchor entirely.

**One armed tool at a time.** Every tool joins the `build_tools` group and
`SetActive(true)` disarms every other member, so two tools can never listen to
the same click and a disarmed tool drops its anchor, ghost and preview. This
lives in the base rather than in the palette on purpose: a new tool gets it for
free, and the palette has to know neither the list nor the rule. Every
state-changing entry point is public and cell-driven, and the whole preview
reads back, so the headless tests can assert what the player would see.

### Fields (`src/world/Field.cs`, `src/ui/build/FieldBuildTool.cs`)

> **Farmland is addressed by one `Field` entity per marked rectangle — never by
> the cell.** One drag creates exactly one `Field`; the cells get
> `TileType.Field` only so the `GridMap` can draw them. Crops, jobs, yields and
> M4's output buffer hang off the entity, reached with `WorldGrid.GetField`.

`Field` ids are creation order and are never reused. What that choice commits us
to — all of it deliberate:

- **Exactly one owner per cell.** `GetField` answers with one field or null,
  which is why the tool opts into `VacantCell`: a rectangle can never be drawn
  over ground another field holds, not even partly.
- **Touching fields are never merged.** Two adjacent rectangles stay two fields,
  because each is separately named, worked and harvested. So **enlarging a field
  means marking another one** — there is no grow operation, by design.
- **A field owns its cells rather than deriving them from the tile layer**, so
  it survives an irregular shape: the rectangle is how the player *draws* one,
  not what a field is allowed to be. Bulldozing shrinks one cell by cell, and a
  field that loses its last cell is dropped — an empty field is not something
  the player can still address.
- **Road access is deliberately not required** to mark a field: nothing works
  one until M4 gives machines jobs, and that is when the rule (if any) belongs.

### Structures (`src/world/Structure.cs`, `src/ui/build/StructureBuildTool.cs`)

> **A building is addressed by one `Structure` entity — never by the cell.** The
> cells get `TileType.Structure` only so the `GridMap` can draw them and
> `PlacementRules` can call them occupied; reach the building itself with
> `WorldGrid.GetStructure`.

One generic placeholder building today; the roster — silo in M5, cleaner, mill
and bakery in M6 — hangs off the same tool, the first single-click one
(`NeedsAnchor = false`).

**Why an entity and not just a tile value.** `TileType.Structure` says that *a*
building is here, not *which*, and a building needs an identity long before it
needs behaviour: M5 sends a vehicle to *a silo*, M6 hangs a recipe off *that*
mill, a save has to name it. `Id` is that handle — creation order, never reused,
resolving to null once the building is gone, so an order pointing at a
demolished mill fails loudly rather than hitting its replacement. Bolting the
entity on later would have meant migrating every bare tile already stamped.

**How a 2×2 lands on this without a rewrite.** A structure owns a *list* of
cells even though the tool hands it one, and everything downstream is already
per cell: `Check` iterates, `TouchesRoad` excludes the placement's own cells,
the ghost draws a cell per plan entry, the registry indexes cell → structure,
demolition walks the footprint. The one line that changes is `Footprint`.

**Where a building parts company with a field** — the one deliberate asymmetry:
a building is **atomic**. A field shrinks cell by cell; clearing *any* cell of a
building demolishes the whole thing, because half a mill is not a mill.
`SetTile` does it, detaching the whole footprint from the registry *before*
writing any tile so the clearing writes cannot re-enter it. With 1×1 footprints
the two rules are indistinguishable — the difference is written now because it
is the multi-tile question, not later when four cells make it urgent.

### Bulldozing (`src/ui/build/BulldozeTool.cs`, `src/world/Removal.cs`)

> **Anything the player placed can be taken back off, and the terrain under it
> is never touched** — the ground comes back exactly as it was, fertility and
> all, because the two layers were never stored together.

**Legality is inverted, so it gets its own rules.** No building rule describes
a removal — `BuildableTerrain` asks whether soil could be built on, `VacantCell`
demands the opposite of what this tool is for — and bending either into shape
would have made it mean two things. So the bulldozer opts into `InBounds` +
`OccupiedCell` and nothing else: **what a cell holds never makes it
un-removable.** Bare rock is refused for holding nothing, not for being rock.

**A mixed drag clears what is there and skips what is not.** All-or-nothing
would be unusable here — clearing a farmyard means dragging over the gaps
between its buildings, and one empty cell would refuse the lot. Hence the sole
use of `AnyCell`, with the ghost drawing only the cells that will actually be
cleared in the legal colour. The same rule applies to the *anchoring* click, so
a drag still has to start on something removable — the first piece to revisit if
bulldozing ever feels fiddly in play.

**Both registry rules reach the player through this one tool** and they differ:
bite a field and what is left is still that field; clip any cell of a building
and the whole building goes. The tool implements neither — every cell goes
through `WorldGrid.Clear`, where both rules already live.

**The refund seam is an M7 stub.** Every removal passes through one function,
`RefundFor(Removal)`, whose return `Apply` credits to the `Economy` — so the day
M7 puts a fraction of a build cost in that expression is the day refunds appear,
with nothing else touched. It pays **nothing** today, deliberately rather than
unfinished: the economics are M7's to design. The seam already carries what a
real rule needs — the tile kind, the entity (kept as the object, so a building
can be priced by *what* it is), the cells freed, and the cell the player hit.
`Removals` is the ledger, **one entry per removal, not per cleared cell**, which
is why clipping two cells of the same building refunds it once.

### Money and build costs (`src/world/Economy.cs`)

> **Building costs money, and "you cannot afford this" is a placement refusal
> like any other** — decided with the rest of the verdict, so the ghost shows it
> before the click instead of the click discovering it. Money itself is a **stub
> number** until M7 gives it a market: this is the plumbing, not the economy.

`Economy` is **the one thing that owns the balance**, so there is one number,
one place it changes, and one thing to serialize when saves arrive. `TrySpend`
takes the money **or refuses and changes nothing**, so the balance can never go
negative however a caller is written; `Credit` ignores non-positive amounts, so
the M7 refund of 0 is a genuine no-op; `SetBalance` is the save-load seam.

**Cost is a validation input, not a post-hoc check.** A tool's `CostPerCell` and
the balance go into `PlacementRules.Check` as a `PlacementBudget` — a *value*,
never a handle on the account, which keeps the evaluator the pure thing it was —
and `Check` refuses with `CannotAfford` when the total is out of reach. Testing
affordability inside `ClickCell` instead would have made the ghost lie, showing
a placement as legal that the click then refused.

Like `TouchesRoad`, affordability is a property of the **whole placement**: the
player buys all of a ten-cell road or none of it, never "the cells that were
individually affordable", so the ghost dims it and blames nobody. It is judged
**last**, after every reason that is not about money, so a drag into water is
refused for the water.

**Priced for the cells it acts on**: under `EveryCell` the whole footprint,
under `AnyCell` only the cells that pass — charging a bulldoze for ground it
merely crossed would be charging for nothing.

**Charged on commit, never on preview.** `ClickCell` spends `plan.Cost` and
nothing else in the tool touches money: however long a drag is held and however
often the ghost is recomputed, nothing moves. A refused click is free.

**The knobs are exported and wired in Main.tscn** (`StartingBalance`, and
`CostPerCell` per tool), so tuning is an editor change, not a rebuild; a tool
carries the same placeholder in its constructor and the scene value wins. A tool
with **no** `Economy` wired builds for free — that is "there is no money in this
scene", not "the player is broke", and it keeps a dev scene working.

## Cell picking (`src/ui/CellPicker.cs`)

One implementation of "which cell is under that pixel", shared by every
mouse-driven tool and the hover readout, so the answer can never drift between
them. A static class, not a node: it holds no state.

The camera ray is intersected with the ground plane at y = 0 **analytically**
(`Plane.IntersectsRay`), then handed to `WorldToCell`. No physics bodies, no
collision layers, no raycast query — picking stays exact, deterministic, and
independent of what happens to be drawn on the cell (a tall rock mesh must not
change which cell a pixel means). Picks are **not clamped** to the map: off-map
picks come back as real coordinates, and callers ask `InBounds`/`GetTerrain`
what that means. The position-driven overloads are what lets a headless test
drive a known pixel.

## Hover readout (`src/ui/CellInspector.cs`)

The debug instrument that confirms the generated world is what the generator
thinks it is: a screen-corner `Label` naming everything about the cell under the
cursor, including the owning `Field`/`Structure` *by name* — the entity, because
that is what the game addresses farmland and buildings by.

Fertility prints only for soil (rock and water have none by definition), and
everything is formatted with `InvariantCulture` so the text is identical on
every machine. **Off the map needs no bounds check** — the terrain layer already
answers `OutOfBounds` there, which prints as `terrain: off the map` while the
coordinates stay real.

Toggled with dev key 8, and it starts **on**, because a dev instrument that
needs arming isn't one. This is *not* the player-facing inspector — field
inspection is M4, building inspection M6.

## Dev smoke tests (`scenes/dev/`, `src/dev/`)

Headless end-to-end checks. Each instances `Main.tscn`, drives it for a few
simulated seconds, prints `PASS`/`FAIL` lines and exits 0/1. **Run all of them**
— the older ones are the regression net for the newer ones.

```bash
godot --headless --path . res://scenes/dev/CameraSmokeTest.tscn
godot --headless --path . res://scenes/dev/WorldSmokeTest.tscn
godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn
```

What each one asserts is in the test file, and is not re-narrated here. What is
*not* in the test file, and costs an afternoon to rediscover:

- **Synthetic input needs the right door.** `Input.ActionPress` works for held
  actions, but event-driven ones only reach `_UnhandledInput` via
  `Input.ParseInputEvent(InputEventAction)`.
- **A tool re-hovers from the real cursor every frame.** A headless driver must
  call `HoverAt` in the same frame as the assertion that depends on it, or
  `SetProcess(false)` to pin the hover.
- **`GhostColor` reads the tints the tool handed the mesh, not the mesh.** Under
  `--headless` the dummy renderer keeps no per-instance colours, and
  `GetInstanceColor` answers black.
- **Wait on sim ticks, not frame counts.** Anything the sim moves has to be
  asserted after *N ticks*, since a frame number now says nothing about how far
  it got. The clock itself is testable as plain arithmetic — it is not a node.
- **Cells are searched, never hard-coded.** The tests find the nearest rock, a
  clear soil run, a run ending in water, free soil beside a road, and so on, at
  the point of use — so a seed change cannot quietly turn an assertion into a
  test of something else. **Reuse those helpers** rather than writing literal
  cells into new assertions.

### Canonical views (`scenes/dev/ScreenshotTest.tscn`)

The visual counterpart: renders the canonical views to PNG so a human — or an
agent — can look at what the game actually draws.

```bash
godot --path . res://scenes/dev/ScreenshotTest.tscn -- <dir>
```

**Must run windowed.** Under `--headless` the rasterizer is a dummy with no
framebuffer to read back, so `FramePostDraw` never fires; `_Ready` detects that,
prints `SCREENSHOT TEST FAILED: --headless cannot render` and quits 1 rather
than waiting for a frame that will not come.

**A view earns its place by showing something no other view can** — a ghost
verdict, a placed building, a paused clock. Each `PASS` line captions what the
frame is meant to show; the caption, not the pixel colour, is what says which
cell killed a drag. Conventions worth keeping: views settle by **time**, not
frame count (the rig smooths on `delta`, so a frame count converges differently
on a fast machine); anything cursor-driven is pinned with `SetProcess(false)`
and fed an explicit cell, since a screenshot run has no mouse; and legal spots
are **searched through the tool**, not hard-coded, for the same reason the smoke
tests search.

For a visual check without a window grab, movie-maker mode renders frames:
`godot --path . --write-movie out/frame.png --fixed-fps 30 --quit-after 90
--resolution 1280x720`.

## Not yet implemented (deliberate)

- **Fields have no behavior** — a `Field` is a named region and nothing more.
  "Workable" starts when machines get jobs; fertility is generated but nothing
  reads it; there is no crop, yield or rename UI. All M4.
- **Structures have no behavior** and there is only the one generic kind. The
  roster and what buildings *do* is M5 (silo) and M6 (cleaner, mill, bakery).
  Multi-tile footprints are unwritten but not designed out — `PlaceStructure`
  already takes any footprint.
- **Validation lives in the tools, not the data layer.** `SetTile` still writes
  anywhere, including off the map, which is what start layout, dev code and
  tests want.
- **Player interaction is the palette plus four tools** and two dev keys. The
  unlock *seam* exists (`ToolAvailability`) and none of the rules — what unlocks
  a tool is M8's. The full HUD pass is M10's; the bar and the two corner
  readouts are all the UI there is.
- **Money is a stub number**: placements are charged and the balance is shown,
  but nothing puts money *in*, and every price is a placeholder chosen to be
  tunable rather than balanced.
- **Removing something refunds nothing**, on purpose: `RefundFor` has the shape
  of a refund and none of the economics, which are M7's.
- **Nothing stops a road being bulldozed out from under a machine** that is
  driving it or has it in an already-computed route, and nothing re-checks a
  building's road access when the road beside it goes. Both are real M5 cases;
  the notes sit on `WorldGrid.Clear`, where whoever writes M5 will be standing.
- **Only machines are on the entity arrays.** Fields and structures are still
  plain objects in `WorldGrid`'s registries; they move when M4/M5 give them
  state worth ticking. RNG streams (#24) and determinism hashing (#25) are the
  rest of M3.
- **Nothing reacts to the season** — the calendar advances and is drawn, and no
  system reads it yet. M4's growth is the first that will.
- **Flow fields**: BFS per machine is fine at this scale; revisit when mover
  count grows.
