# Implementation notes

What exists in the codebase today and how it fits together. Companion to
`tech.md` (the engine decisions) — this documents how those were realized, and
the design decisions taken since.

**Every section is written to be read *alone*** — find it in the index, read
it, stop (`sed -n '/^## World grid/,/^## /p'`). It holds only what reading `src/`
does not recover: why a thing is shaped as it is, what was rejected, what is
deferred. CLAUDE.md carries the rest of the rule and the budget.

Last updated: 2026-09-07.

## Index

| Section | Read it when |
|---|---|
| `## Building & running` | building, running or adding a scene |
| `## Project layout` | looking for where something lives |
| `## Simulation` | touching the tick, sim state, or anything the view draws |
| `### Entity storage` | adding an entity kind, or asking what is near a cell |
| `### State hashing` | hashing sim state, or chasing a determinism bug |
| `### Items and buffers` | carrying, storing, moving or counting a good |
| `## Game calendar` | touching days, seasons, game speed or pause |
| `## Randomness` | drawing a random number, or seeding anything |
| `## Camera` | touching `CameraRig`, projection or input |
| `## World grid` | touching world state, terrain, tiles, roads or how a cell draws |
| `## Machines` | touching vehicles or movement |
| `## Labour and wages` | hiring, the payroll, or anything that costs per day |
| `## Build palette` | touching the toolbar or tool selection |
| `## Dev shortcuts` | adding a dev key |
| `## Build tools` | adding or changing a placement tool, or a placement rule |
| `### Fields` | touching farmland |
| `### Crops` | touching the crop lifecycle, a stage, growth or the yield |
| `### Structures` | touching buildings |
| `### Bulldozing` | touching removal or refunds |
| `### Money and build costs` | touching prices, the balance or affordability |
| `## Cell picking` | mapping screen to cell |
| `## Hover readout` | touching the debug readout |
| `## Field panel` | touching what a field reports to the player |
| `## Dev smoke tests` | writing or fixing a test — **read before writing one** |
| `## Not yet implemented` | before assuming something is missing by accident |

## Building & running

- **Godot 4.7 stable (.NET/mono build)**, C# on **.NET SDK 8**. The
  `.csproj`/`.sln` are hand-written: `--build-solutions` will not create them
  from scratch.

## Project layout

The namespace is flat `Arable` throughout, so moving a file between folders is
free. `dev/` is test scenes, not the game; `scenes/Main.tscn` is the entry point.

**Gotcha (hand-written .tscn):** a Node-typed export serialized as
`World = NodePath("../World")` only resolves if the `[node]` header also carries
`node_paths=PackedStringArray("World")`; without it the property loads as null.

## Simulation (`src/sim/`)

> **The view reads sim state; it never writes it** — the same rule `WorldGrid`
> holds over its `GridMap`, one level up and now enforced by types:
> `ISimSystem.Tick` is the only place sim state may change.

Why `SimClock` and the `Simulation` node that drives it are shaped this way:

- **20 Hz, and deliberately not 60.** Matching Godot's physics rate would let a
  coupling bug hide behind the coincidence; at 20 Hz anything accidentally
  running per physics step is off by 3×. An export, not a constant.
- **Fed from `_Process`, not `_PhysicsProcess`**: clocking off a fixed step the
  frame loop already owns would make the sim a divisor of the render rate.
- **Overrun is dropped, not owed.** Past `MaxTicksPerFrame` (5 = 0.25 s of sim)
  the surplus is discarded and counted in `DroppedTicks`; a backlog would make
  the next frame slower still, which is the spiral of death. So a sim that
  cannot keep up runs **slow**, never at a variable step: determinism survives
  and only the tie to the wall clock is lost. `DroppedTicks > 0` is the signal.
- **The view lags one tick**: the blend ends at the *current* state, so the
  picture is up to 50 ms behind. Extrapolating would mispredict corners.
- **`Alpha` can be exactly 1.** Deltas summing to a whole tick can fall a hair
  short in doubles and round to 1 in single precision. Clamped and harmless —
  but measure sim time as *ticks + alpha*, never as whole ticks alone.
- **Found by group, not `NodePath`**: entities are instanced at runtime. It ticks
  at `ProcessPriority = -100`, so a transform read this frame is the sim's.
- **Registration during a tick is unsupported** — an entity that wants to stop
  goes idle inside its own `Tick`.

### Entity storage (`src/sim/EntityStore.cs`, `EntityId.cs`, `SpatialHash.cs`)

Sim entities are rows in flat arrays, not nodes. What forces it is not frame
cost but **walkability** (`### State hashing`): a scene subtree gives an order
that depends on spawn history, plus an allocation per entity.

- **`EntityStore` owns liveness and nothing else**, each system keeping its own
  parallel arrays. No component registry, no archetypes, no queries — tech.md asks for a
  data-oriented *layout*, not an ECS framework, and generalising before M5 has
  said what components exist would be guessing.
- **The generation is the point of the handle.** Slots are reused, so an index
  alone cannot tell "the machine I spawned" from whatever moved into its slot
  after it died; that bug reads as teleportation. `Destroy` bumps the slot's
  generation, invalidating every stale handle at once, and live generations
  start at 1 so `default(EntityId)` is dead though slot 0 is real. A recycled
  slot still holds the old values, so a spawn must write **every** column.
- **Walk by slot, never by dictionary.** `SlotCount` + `IsAliveSlot` is the only
  iteration order offered: ascending, independent of creation order. Enumerating
  a `Dictionary` is the quiet way to lose determinism.
- **`SpatialHash` is a bucket of ids per grid cell** — keyed by cell because
  `WorldGrid` already owns the rounding rule, and a second granularity would be a
  second rule to keep in step. *Derived*: rebuildable, never saved, never hashed.
  Queries walk an ascending cell range rather than enumerating buckets, and
  emptied buckets are dropped or a roaming mover grows it to map size.

### State hashing (`src/sim/SimStateHash.cs`, `StateHash.cs`, `IHashableState.cs`)

> **Determinism is a claim nobody can eyeball, so it is a number.**
> `SimStateHash.Of(sim)` walks all sim state into 64 bits, taken after *every*
> tick of two runs from one seed — one hash at the end answers only "did they
> diverge", and bisecting by hand is the afternoon this prevents. M10 takes the
> same number either side of a save/load, hence a `Simulation` argument and
> nothing test-shaped.

**Order independence is the whole difficulty**: two runs holding identical state
can enumerate a `Dictionary` differently, so hashing that walk in order reports a
divergence that is not one. Entities go in by slot walk, streams by
`RandomStreams.Ordered`, anything else by a commutative *add* of mixed member
hashes plus a count — xor cancels a member against its duplicate.

**State hashes itself** (`IHashableState`) rather than a walker reaching into
everyone's arrays, so the milestone that adds a column adds one line where it is
already editing.

**Left out on purpose.** Speed, the tick accumulator and `DroppedTicks` are
real-time quantities — hashing them fails the harness on a slow frame, which is
how a determinism check gets switched off. Derived state (routes, the spatial
hash, cell → owner lookups) is out because M10 will not save it and a correct
rebuild would then fail the comparison; the cost is a *different* route showing
one tick late. Floats go in **by their bits**: rounding hides the drift.

**Trap for M10:** the entity free list is not hashed, only *which* slots are
free. A load rebuilding it in another order hashes equal today and hands out
different slots tomorrow, so restore it in destruction order.

### Items and buffers (`src/sim/ItemType.cs`, `ItemBuffer.cs`)

> **A good is a type id and a quantity in a container with a capacity, never a
> counter.** A global grain total leaves M5's trucks nothing to collect from and
> M6 no way to tell grain from the flour it becomes.

**A type is a catalogue row, not an enum**, which would need editing — with
every switch over it — each time a recipe adds a good; an id indexes parallel
arrays (M7's price is another one). Ids are **stable**, appended and never
reordered, because a save writes the number.

**Capacity counts units, not stacks, and exists before anything consumes it**:
"the output is full, so production stops" is what M6's chains are made of.
Rejected: per-good limits, needing a rule for splitting a mixed buffer's room
that nothing has asked for. Contents are **never dropped to fit** a capacity
lowered under them, and a surplus is never spilled: both are grain evaporating,
which is the logistics game deleting itself.

**A move between two buffers is `Transfer`/`TryTransfer`, never a `Remove`
beside an `Add`** — a hand-written pairing is where the halves disagree about how
much moved, and the item lost that way surfaces much later as a hash divergence
with nothing pointing at the line. **The destination is credited first and the
source debited by what it accepted**: taking first and then failing to deposit
loses items, while this order only double-counts, across two statements with
nothing in between. Hence a buffer transferring to *itself* answers 0 —
add-then-debit would "succeed" by handing back its own room — and one call moves
one good, a mixed drain being the caller's priority decision.

**Every carrier holds the same type**: a field's crop row, a vehicle's `Cargo`,
a building's `Storage`, so a haul is one call whatever its ends are. Each is
sized from an export, and a truck's is deliberately under a grown field's so
clearing one is several trips. Per-kind sizes are M6's roster, and a building's
*single* store is not an input/output pair until M6 says which side a silo's is.
A row takes a **fresh** buffer at spawn, never the recycled slot's emptied out: a
generation invalidates a stale *handle*, not a reference to the object behind it.

**Deferred:** an item having a position of its own; until then a unit exists
only inside some buffer, which is what makes "nothing was lost" testable.

## Game calendar (`src/sim/GameCalendar.cs`, `src/ui/TimeControls.cs`)

> **Two clocks, and conflating them is the trap.** `SimClock` is the *tick
> scheduler* — real seconds in, whole ticks out. `GameCalendar` is the *date* —
> ticks in, days and seasons out. Neither measures what the other does, which is
> exactly what lets the speed control move the first and never the second — and
> makes a day a count of ticks rather than of seconds, so the same ticks elapse
> per simulated day at every speed by construction, 3× only running them sooner.

**The numbers, and why.** A day is **600 ticks** — 30 s at 20 Hz, about one haul
across the map, so a five-minute playtest sees ten and day-scale effects (M7's
price drift, M8's deadlines) are observable in a sitting. 600 and the **12-day**
season factorise hard, so sub-day schedules and whole-day growth stages land
without remainder. Both are `[Export]`s; the season *count* stays at four,
because which seasons exist is content.

**The whole of the calendar's state is one `long`**; everything else is
division, because #25 must hash sim state and M10 must save it and an integer is
the cheapest thing either can walk. Day and year read **1-based** while
`TotalDays` is 0-based — mixing them is off-by-one in every printed date. And
there is deliberately **no rollover signal**: a system that cares keeps the day
it last acted on in its own state (`## Labour and wages` first), which saves and
hashes with it, where an event would be re-fired by a load, replay or date jump.

**Speed scales the schedule, never the step.** `SimClock.Speed` multiplies the
real time booked, so 2× runs twice the ticks per real second with `TickDelta`
untouched; a variable step would make a run depend on the speed the player
happened to watch at. Rejected: `Engine.TimeScale` (scales the *frame* delta, so
camera smoothing speeds up with the sim) and `SceneTree.Paused` (stops the camera
too — a paused world still has to be lookable-around). `MaxTicksPerFrame` is
deliberately *not* scaled with it, so at 3× it bites below ~12 fps and the sim
runs slow, visibly via `DroppedTicks` — never by shortening a day. And **speed is
not sim state** (`### State hashing`); the calendar is the opposite.

**The controls copy the palette** (`## Build palette`). **Step 0 is always
pause**: the ladder is otherwise free-form, but the code must find the stop, so
one not starting at 0 is repaired at load with a warning. **Deferred:** a pause
*key* (the number row is the palette's) and M9's seasonal hazards.

## Randomness (`src/sim/RandomStream.cs`, `RandomStreams.cs`)

> **One world seed; every system draws from its own named stream off it.** A
> shared pool is the determinism trap this removes: every consumer walks one
> sequence, so an extra roll anywhere shifts every later draw. The game still
> runs — it is just no longer the game that was saved, and what breaks first
> (M7 prices, M9 pests) is what nobody can eyeball for correctness.

**Derivation, not partitioning.** A sequence comes from `(worldSeed, name)` and
nothing else — not a slot in a list, not registration order, not how many
streams exist — so a system added in M9 cannot disturb one written in M3.

**PCG32, written into the repo.** `System.Random(int)`'s sequence is documented
as an implementation detail (the unseeded one already changed in .NET 6), so a
save that must replay after a runtime upgrade cannot rest on it. Against
xoshiro/xorshift, PCG wins the two points that matter: **every 64-bit value is a
legal state**, so a restore can never land in the all-zeros trap, and **the
increment is a stream selector**, so deriving it from the name gives a system its
own sequence rather than an offset into one shared cycle. Restoring assigns its
single `ulong` *mid-sequence*; `Reseed` means *new world*, not *load*.

**A class, not a struct:** a mutable value type advances a *copy* as soon as it
is passed or pulled out of a collection, surfacing as a determinism failure
and not as anything that looks wrong. Streams are per **system**, not per entity
— a slot walk is reproducible already — and their names live with the owning
system, not a central list; a typo therefore opens a *new* stream, hence constants.

**Three traps.** `WorldGrid.WorldSeed` forwards to the seed on `Simulation`
(terrain is its loudest consumer, not its owner), but that `[Export]` is the
**authored** seed and `Streams.WorldSeed` the live one — a reseed does not write
back. Out of the tree there is no `Simulation` to forward to: a *read* is silent
(the throwaway registry answering it is dropped on entry), a *write* is dropped
with it and warns. **`FastNoiseLite` is not a stream** (one int in, its own field
out), so terrain takes its int from `DeriveSeed` rather than an offset like
`WorldSeed + 7919`: adjacent noise seeds are not guaranteed unrelated. And
**never `string.GetHashCode`**: randomised per process, it would seed a
different stream every launch, a bug reproducing in no single run — the name
hash is FNV-1a over the bytes, pinned to golden values in the smoke test.

**Deferred:** nothing writes a stream anywhere yet (M10).

## Camera (`src/camera/CameraRig.cs`)

**Structure.** The rig yaws; the camera under it is fixed at true-isometric
pitch (35.264° = atan(1/√2)), at 45° so 90° steps keep the iso diamond. **The
three things to know before changing it:**

- Zoom changes the camera's orthographic `Size`, not its position, and keyboard
  pan speed scales with zoom so screen-space speed feels constant.
- Drag pan converts pixels to world units via `Size / viewport height`, dividing
  the vertical component by `sin(pitch)` to undo the iso foreshortening — that
  division is what makes the ground stick to the cursor.
- Zoom and rotation smooth with frame-rate-independent decay (`1 - exp(-k·dt)`),
  over *unwrapped* target yaw, so repeated rotations accumulate rather than fight.

## World grid (`src/world/`)

Four entity kinds: **roads** are tiles; **fields** and **structures** are a tile
*plus* an entity owning those cells, the tile saying only *that* something is
placed; **machines** are sim rows and deliberately not grid cells. A field is
both — a registry entry here and a crop row in the sim (`### Crops`).

**Data/view split.** The child `GridMap` is presentation only and must
**never be read back** — every mutator keeps it in sync (`## Simulation`).

**Two layers, stored separately.** What the land *is* (terrain plus fertility,
from the seed) and what the player *built* (`TileType`, sparse) never share
storage — which is what makes bulldozing lossless: clearing back to `Empty`
leaves the terrain untouched. One `GridMap` draws both, placement *hiding*
terrain rather than overwriting it. `GetTerrain` answers `OutOfBounds` rather
than throwing, so "not on the map" is one call.

**Terrain generation** (`GenerateTerrain`, run before the start road):

- The map is **bounded**: `MapHalfExtent` 48 → 97×97 cells = 194 m at 2 m cells,
  fitting Main.tscn's ground plane and slightly larger than full zoom-out shows.
- **One seed** drives everything: a fertility noise and a rock/water mask, each
  taking its int from `## Randomness`. The mask reads as a coarse elevation — low
  is water, high is rock, the rest soil — so water and rock never border each
  other. Fertility is **0 on rock and water**: it means "how good is this soil".
- Storage is **flat arrays** indexed by cell: the extent they were built with is
  cached, so moving `MapHalfExtent` at runtime can never index past them.
- Re-running it is **bit-exact** for a seed and never touches the placement
  layer. The starting road strip (z = 0, x = -16..16) is **carved** to soil
  rather than biasing the noise, so the seed still owns every other cell.

**A field cell draws its crop stage** (`### Crops`): one MeshLibrary item per
`CropStage`, consecutive from a base id, the arithmetic the fertility tiers
already use. `TileType` stays at `Field`, because six stages in the tile enum
would put sim state in the placement layer; the plain farmland item is the
fallback for a cell whose row is unreachable.

**How a stage the sim moved reaches the `GridMap`.** Every other tile changes
because something called `SetTile`; a crop ripens because time passed and nothing
calls anything. So `WorldGrid` takes the `ISimView` half as well and sweeps its
fields once a frame against the stage it last drew, pushing only those that moved
— view bookkeeping, never hashed, never saved. Rejected: an event out of
`CropSystem`, which would run the view half-way through a tick and point the
dependency from the sim at the renderer. `MarkField` draws its own new field
rather than waiting for a frame, because a stepped run has none.

**Stage art needs height *and* colour**: at `ZoomMax` only colour reads, close in
height is what separates two browns. **Keep the ramp out of the terrain palette**
— grey is rock, olive is soil — a trap paid for once, when a grey-brown fallow
field read as a rock outcrop at the zoom limit. Warm earth → green → gold is
what is left, and M10 inherits it.

**Road-network queries live here**, because they are questions about the grid
rather than about a vehicle:

- `FindRoadPath` — BFS over road cells, 8-neighbour, where a diagonal step is
  allowed only past a road *corner*; that rule is what stops a path squeezing
  through the point gap between two unconnected strips. `SmoothRoadPath` then
  string-pulls it, dropping every waypoint the vehicle can drive straight past.
- `LineCells` — Bresenham, except a diagonal step stair-steps through two
  orthogonal cells so the footprint stays 4-connected, and the connector cell
  **alternates sides** on successive diagonals so the staircase stays centred on
  the true line; without that, machines drive hugging one edge of the band.
- `RectCells` — row-major from the minimum, so the list depends on the
  rectangle and not on the corner the drag started from.

**Mutators.** `SetTile` is the single write path; `MarkField`,
`PlaceStructure` and `BuildRoadLine` are the **unvalidated** doors, for start
layout, dev keys and tests, while anything the *player* places goes through a
`BuildTool` and so `PlacementRules`. `Clear` returns **null** — not an empty
`Removal` — for a cell that held nothing, which is load-bearing below.

Two asymmetries in `SetTile` that everything downstream inherits: a field cell
written over detaches **just that cell** (and a field with no cells is dropped),
while **any** cell of a structure demolishes the building. The null is what stops
one building being refunded once per cell a drag clipped.

## Machines (`src/world/MachineSystem.cs`, `src/world/Machine.cs`)

A machine is any vehicle. **State and drawing are two objects**: rows in
`MachineSystem`'s arrays (`### Entity storage`), drawn by a `Machine` node that
is an `ISimView` and nothing else. `WorldGrid` constructs and registers the
system, because machines run on the road queries it already owns.

- **The node transform is a drawing, not a position.** `Interpolate` writes it
  each frame from the row's last two sim poses; `SimPosition` is where the
  machine is. The exports are spawn *input*, read once off the instanced scene
  and copied into the arrays — changing one on a live node does nothing.
- **A machine carries a load** it cannot yet fill or empty (`### Items and
  buffers`): the column and its place in the hash are here so #34's orders add
  the *moving*, not the storage, and a truck that hauled shows in the hash.
- **Behavior:** wander. Smoothing keeps a stair-stepped diagonal road from being
  driven as a zigzag. A tick spends a travel budget (`Speed·dt`) across waypoints
  so corners lose no distance, and the previous pose is snapshotted for *every*
  machine — a parked one drifts.
- **Freeing the node despawns the row.** `Machine._ExitTree` is the one place a
  view touches sim state — lifecycle, not a state write. Nothing else notices a
  freed node, and an orphan row is an invisible machine still driving the roads.
- **Determinism:** one named stream for the whole system (`## Randomness`), drawn
  in slot order. Spawn placement comes off the *world's* stream instead, so
  spawning a machine cannot reroute the ones already driving.

## Labour and wages (`src/sim/LabourPool.cs`)

> **A hired worker is pure cost until the player gives it a vehicle** — the M5
> lesson, not a gap. No job pool and no auto-assignment; #33 is where the player
> types one, and a pool that found its own work would teach the opposite.

**A `Node`**, not a plain class like `CropSystem`: a worker is on nothing, so
`WorldGrid` was no owner for it, while the fee and wage want to be inspector
numbers and the `Economy` scene wiring. **Workers are interchangeable** — one
wage, and a row's only column is the day hired; a skill is content nothing can
consume until M6 varies the work. **The boundary is `TotalDays` against a stored
`_lastPaidDay`** (`## Game calendar`), compared rather than watched for, so a
date jump charges every skipped day and whoever is on the books when it falls
pays a whole one; prorating is a per-worker accrual nobody needs yet.

**The fee is refusable, the wage is not** — you cannot hire what you cannot pay
for, but a day passing is not a request. Hence **insolvency: the balance floors
at zero and the shortfall becomes `Arrears`**, rolled into the next boundary, so
a debt settles itself once income exists. Rejected: a negative balance (M2 made
"never negative" an invariant every placement check reads through `CanAfford`),
auto-firing the unpaid (the sim repairing the player's payroll — the convenience
M5 forbids), and forgiving the day (insolvency free just as it starts to bite).
With no `Economy` wired labour is free, as a tool without one builds free. What
arrears *cost* is M7/M8's — a consequence needs a market to be one in; the hire
UI is M10's, and the pool is finite (four) so hiring is a decision.

## Build palette (`src/ui/BuildPalette.cs`)

> **The toolbar is how a build tool is chosen**, and it is the pattern every
> other HUD bar copies (`## Game calendar` is the second): one button per entry
> from an exported list, one selection path, and a repaint every frame.

**Built in code from the exported `Tools` list**, so putting M5's silo on the bar
is adding it to that list; each button reads its name, icon and price off the
tool, so the bar cannot quote a price the click does not charge.

**It is not a second source of truth.** Which tool is armed is a fact about the
tools (`BuildTool.Active`, kept unique by the `build_tools` group), repainted
every frame, so a tool disarmed by *any* route is right on the bar the same
frame, with no palette-side copy to disagree.

**Keys are an accelerator for the bar, not a way around it.** Key *n* — and a
headless test — calls the same `Toggle` a button does; `menu_1` upwards grows
with the roster.

**The M8 seam is `ToolAvailability`.** A non-available entry is refused on
**every** path in, because a seam only one path respects is decoration; a tool
that stops being available while held is disarmed on the spot. The *rules* are
M8's: nothing here knows what an unlock is.

**Two gotchas in the look.** Icons are tinted with a *modulate*, which
multiplies, so **the source artwork has to be white** — a dark glyph stays dark
whatever colour it is given. And the root `Control` is `MOUSE_FILTER_IGNORE`, as
are the inner labels, or a full-screen `Control` swallows every click.

## Dev shortcuts (`src/ui/DevShortcuts.cs`)

What is left of the old number-key menu, none of it a build tool: the keys sit
at the *top* of the number row because the palette claims it from the bottom
up. Key 7 is the hand M5 replaces — it runs whatever
the field under the cursor is waiting for, through the door an order will take.

## Build tools (`src/ui/build/`, `src/world/PlacementRules.cs`)

Every mouse-driven placement tool sits on one base, `BuildTool`, whose point is
that an illegal placement is refused *before* the click rather than by it;
copying `RoadBuildTool` is how the next tool gets written.

**The rules** are a `[Flags]` set (`PlacementRule`) — add a flag rather than
re-code a check in a tool — and the near-duplicates in it are the point.
`BuildableTerrain` has the looser `InBounds` beside it for the bulldozer: what
may be *taken off* a cell says nothing about what could be built there.
`NoOverlap` and `VacantCell` differ by exactly the "already there" case, which
lets a road branch off the network while a field's cell belongs to one `Field`.
`TouchesRoad` is **footprint-level** and 4-neighbour: the road must be *outside*
the footprint, or a placement would satisfy its own access — and a corner is not.

`Check` answers a `PlacementPlan`, and how its per-cell verdicts add up is the
`FootprintPolicy`, because building and clearing want opposite answers about a
mixed region. Building is `EveryCell`: **all-or-nothing**, since a field with a
bite out of it is not what the player asked for, and the per-cell verdicts exist
only so the ghost can point at the offenders. The bulldozer alone is `AnyCell`,
*priced* on the cells it acts on rather than the ground it crossed.

**What the player sees** is a ghost whose one promise is that **a legal-coloured
cell is a cell something will happen to** — hence a virtual `GhostColorFor`:
under `AnyCell` a legal plan still holds cells it will skip.

**Interaction.** A refused click writes nothing *and keeps the anchor*, so the
player re-aims; right click / Esc drops the anchor first, the tool second.

**One armed tool at a time.** Every tool joins the `build_tools` group and
`SetActive(true)` disarms the rest. This lives in the base rather than the
palette on purpose: a new tool gets it free, and the palette knows no rule.

### Fields (`src/world/Field.cs`, `src/ui/build/FieldBuildTool.cs`)

> **Farmland is addressed by one `Field` entity per marked rectangle — never by
> the cell.** Jobs and yields hang off the entity, reached with
> `WorldGrid.GetField`; what a crop is doing on it is one step further out
> again (`### Crops`).

`Field` ids are creation order and never reused. What that commits us to:

- **Exactly one owner per cell**, which is why the tool opts into `VacantCell`:
  a rectangle can never be drawn over ground another field holds, even partly.
- **Touching fields are never merged**, because each is separately named, worked
  and harvested. So **enlarging a field means marking another one**.
- **Road access is deliberately not required** to mark a field: nothing works
  one until M5 gives machines orders, which is when the rule (if any) belongs.

### Crops (`src/sim/CropSystem.cs`)

> **A field is a state machine, and the interesting half is what it refuses.**
> Ploughing, sowing and harvesting are *work*, sprouting and ripening are
> *time*, and `Apply` is the one door either kind takes. A refusal is its return
> value, not a warning: M5 will ask "can this be sown" far more often than it
> sows, and a line per refusal buries the log the first time a scheduler polls.
> Wheat only, and no crop-kind column — a one-valued enum is a member every
> later milestone keeps in step for nothing.

**Crop state left `Field` for the entity arrays** (`### Entity storage`). An
enum and a float on `Field` would have hashed fine through `WorldGrid`'s
registry fold — and left the growth factors, the output buffer and M5's jobs
walking a `List` whose order a load need not reproduce. The pairing is hashed
from both ends, because neither side derives the other.

**Harvest leaves stubble, to be ploughed in again.** Rejected: returning the
field straight to sowable, which makes ploughing a once-per-field job when M5
exists to program a *repeating* round — hence `StubbleNeedsPloughing`.

**Growth accumulates in whole ticks, not days**: adding 1/600 of a day a tick
lands a hair either side of the threshold in single precision, so a crop ripens
a tick early or late by whichever way the last rounding fell — invisible, and
untestable. A full-rate tick banks 1; the factors are fractions of it.

**A tick banks `base × fertility × season × water`, and multiplying is the
point.** A factor at zero *stalls* the crop where it stands instead of slowing
it, which is what stops an untended field quietly finishing and keeps M5's
labour worth programming; summing would let three good ones carry a zero. Every
factor is an `[Export]` on `WorldGrid`: M4's question is the cadence, and no
answer should need a rebuild. Shipped seasons run full, full, half, **nothing** —
winter at 0 makes *when* to sow a decision, and is the first number to soften if
the dead season plays long. **Water is inert**: nothing in the POC irrigates or
rains, but dropping the term makes whoever adds rain re-open product and hash.

**Fertility reaches the row as a column, never as a lookup** — `src/sim/` knows
nothing of cells — and the number is the mean of the field's **chunk** means, a
chunk being a `FertilityChunkSize`-cell square. Rejected:
the plain cell average, which lets a field lean its rate on whichever patch it
clipped most of, so the same two patches answer differently depending where the
drag started; equal weight per chunk makes the rate a property of the ground the
field spans. Chunks align to the world origin, so the same ground falls in the
same chunks whoever marks it, and size 1 is the exact cell mean — the knob's off
position. The season is read live off `GameCalendar` instead: it is sim state,
and a copy taken at startup is wrong the first time a save loads in autumn.

**A harvest deposits a stack into the field's own buffer** (`### Items and
buffers`), reached through the crop row like the stage, and takes that section's
atomic door — **refused whole** when the yield will not fit (`OutputFull`),
leaving the crop standing ripe, because a partial fill would need a half-cut
field the state machine has no stage for. M4's only backpressure, and M5's
collection clears it. **Yield is the ground, not the growth banked** —
`YieldPerCell × area × fertility`, floored — because banked growth stops at ripe
and is the same for every field that finished, while the ground pays fertility
twice: good soil ripens sooner *and* cuts heavier. Season stays out, so winter
delays a harvest instead of shrinking one; `ProjectedYield` is that same sum
(`## Field panel`). **Area is a second pushed-down column** sizing the buffer at
`FieldOutputHarvests` *perfect* harvests, so only an uncollected second cut bites.

**Traps.** A row takes its ground at `MarkField` and whenever the field's cells
change, never per tick — so moving `FertilityChunkSize` at runtime re-aggregates
fields marked *after* it, not those already standing. The size is frozen onto the
`Field` at marking and re-read from there; without that, the live export would
re-chunk a field at a granularity it was never marked at the moment one corner
of it was bulldozed. Neither the multiply order nor the chunk walk
may be reordered (float arithmetic is not associative), which is why the walk
sorts the cells by (chunk row, chunk column, input index) and never enumerates a
dictionary — the index breaks every tie, so summation order is a function of the
cells alone. Sorted rather than bucketed into an array over the chunk bounding
box, which it did first: cells off the map are legal, so two far-apart ones size
that array by the *gap* — an unbounded width × height that wraps negative first.
The factors hash beside the thresholds, or two differently tuned worlds hash
alike. And the harvested good is configuration, not a column, exactly as the crop
kind is — both become columns the day a second crop lands.

### Structures (`src/world/Structure.cs`, `src/ui/build/StructureBuildTool.cs`)

> **A building is addressed by one `Structure` entity — never by the cell**,
> reached with `WorldGrid.GetStructure`; the cells carry `TileType.Structure`
> only so the view can draw them and `PlacementRules` can call them occupied.

**Why an entity and not just a tile value.** `TileType.Structure` says that *a*
building is here, not *which*, and a building needs an identity long before it
needs behaviour: M5 sends a vehicle to *a silo*, M6 hangs a recipe off *that*
mill, a save has to name it. `Id` is that handle — creation order, never reused,
resolving to null once the building is gone, so an order pointing at a demolished
mill fails loudly rather than hitting its replacement. It carries a `Storage`
buffer (`### Items and buffers`) hashed with the rest of the registry entry, so a
building is state before it is an entity row.

**Where a building parts company with a field** — the one deliberate asymmetry:
a building is **atomic**. A field shrinks cell by cell; clearing *any* cell of a
building demolishes the whole thing, because half a mill is not a mill.
`SetTile` detaches the whole footprint from the registry *before* writing any
tile, so the clearing writes cannot re-enter it. With 1×1 footprints the two
rules are indistinguishable; the difference is written now, not when four cells
make it urgent.

### Bulldozing (`src/ui/build/BulldozeTool.cs`, `src/world/Removal.cs`)

> **Anything the player placed can be taken back off, and the terrain under it
> is never touched** — the ground comes back as it was, fertility and all,
> because the two layers were never stored together.

**Legality is inverted, so it gets its own rules.** No building rule describes
a removal — `BuildableTerrain` asks whether soil could be built on, `VacantCell`
demands the opposite of what this tool is for — and bending either into shape
would have made it mean two things. So the bulldozer opts into `InBounds` +
`OccupiedCell` and nothing else: **what a cell holds never makes it
un-removable.** Bare rock is refused for holding nothing, not for being rock.

**A mixed drag clears what is there and skips what is not.** All-or-nothing
would be unusable — clearing a farmyard means dragging over the gaps between its
buildings, and one empty cell would refuse the lot. Hence the sole use of
`AnyCell`, the ghost colouring only the cells that will actually be cleared. The
*anchoring* click is the exception — a drag has to start on something removable,
the first thing to revisit if bulldozing ever feels fiddly.

**The refund seam is an M7 stub.** Every removal passes through `RefundFor`,
whose return `Apply` credits to the `Economy`: the day M7 puts a fraction of a
build cost in that expression is the day refunds appear. It pays **nothing**
today and is handed the entity, so a building can be priced by *what* it is.
`Removals` is the ledger, **one entry per removal, not per cleared cell**.

### Money and build costs (`src/world/Economy.cs`)

> **Building costs money, and "you cannot afford this" is a placement refusal
> like any other** — decided with the rest of the verdict, so the ghost shows it
> before the click instead of the click discovering it. Money itself is a **stub
> number** until M7 gives it a market: this is the plumbing, not the economy.

`Economy` **owns the balance**, so there is one number to serialize; `TrySpend`
takes the money **or changes nothing**, so it cannot go negative — an invariant
`## Labour and wages` works around rather than relaxes.

**Cost is a validation input, not a post-hoc check.** A tool's `CostPerCell` and
the balance go into `PlacementRules.Check` as a `PlacementBudget` — a *value*,
never a handle on the account, which keeps the evaluator the pure thing it was —
and `Check` refuses with `CannotAfford`. Deciding it inside `ClickCell` instead
would have made the ghost lie, showing as legal a placement the click refused.
Like `TouchesRoad` it is a property of the **whole placement** (all of a ten-cell
road or none of it), judged **last**, so a drag into water blames the water.

**Charged on commit, never on preview**, so a refused click is free however often
the ghost is recomputed. A tool with **no** `Economy` wired builds for free: that
is "there is no money in this scene", not "the player is broke".

## Cell picking (`src/ui/CellPicker.cs`)

One implementation of "which cell is under that pixel", shared by every tool and
the hover readout, so the answer cannot drift between them. The camera ray meets
y = 0 **analytically** — no physics bodies — so a pick is exact, deterministic
and independent of what is drawn there (a tall rock mesh must not move a cell).
Picks are **not clamped**: off-map ones are real, and callers ask `InBounds`.

## Hover readout (`src/ui/CellInspector.cs`)

The debug instrument that confirms the generated world is what the generator
thinks it is. It names the owning `Field`/`Structure` — the entity, not the cell
— and formats with `InvariantCulture`, so the text reads the same everywhere.
**Off the map needs no bounds check**: the terrain layer answers `OutOfBounds`.
Not the player-facing panel; that is `## Field panel`, and M6's.

## Field panel (`src/ui/FieldInspector.cs`)

> **It reports facts about the field and never points at the culprit.** A
> stalled crop reads "stalled": no season named, no factor blamed, no advice.
> M6's building panels inherit that rule under a much sharper version of it, and
> a habit the cheapest panel in the game breaks is not one.

**Days remaining are the growth model's live rate** (`GrowthPerTick`), never the
nominal schedule — a countdown is read exactly when the ground or the season is
why the schedule is wrong. **A zero rate is a word, not a division**: dividing by
one is how a panel promises a harvest in eight thousand days, so `Stalled` is
settled first, and the stages time does not move get no countdown.

**Yield is `ProjectedYield` itself, never a second formula** over the same inputs
(`### Crops`), so what is shown and what lands cannot drift. The buffer line is
there for the same reason: a full buffer refuses the next harvest whole, and
nothing else shows that coming. **A view, and only a view**, repainted every
frame like the time bar, so a countdown runs down and a bulldozed field closes
the panel unprompted. Selection is `## Cell picking`; an **armed build tool owns
the click**. **Deferred:** renaming a field, and every building panel — M6's.

## Dev smoke tests (`scenes/dev/`, `src/dev/`)

Headless end-to-end checks, each printing `PASS`/`FAIL` lines and exiting 0/1.
**Run all of them** — the older ones are the regression net for the newer ones.

CLAUDE.md has the command and names them; what each asserts is in the test file.
What is *not*, and costs an afternoon:

- **A warning fails CI.** `run_godot.sh` reds the build on any logged
  `WARNING`/`ERROR`, since a scene quits 0 over a failed load. So a `PushWarning`
  must be narrow enough never to fire in a healthy run.
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
  the second world wires itself to the first world's clock and streams. Free one
  and wait a frame before instancing the next.
- **Cells are searched, never hard-coded.** The tests find the nearest rock, a
  clear soil run, a run ending in water, free soil beside a road, and so on, at
  the point of use — so a seed change cannot quietly turn an assertion into a
  test of something else. **Reuse those helpers** rather than writing literal
  cells into new assertions.

### Canonical views (`scenes/dev/ScreenshotTest.tscn`)

PNGs of the canonical views, so a human — or an agent — can look at what the
game actually draws.

**Must run windowed**: `--headless` gives a dummy rasterizer with no
framebuffer, so `FramePostDraw` never fires. `_Ready` detects it and quits 1.

**A view earns its place by showing something no other view can** — a ghost
verdict, a paused clock, six crop stages abutting at three zooms. Each `PASS` line captions what the
frame is meant to show; the caption, not the pixel colour, is what says which
cell killed a drag. It keeps the conventions above, plus one of its own: views
settle by **time**, not frame count, because the rig smooths on `delta`.

## Not yet implemented (deliberate)

- **Nothing moves an item, and no machine works a field.** Every carrier has a
  hold and there is one call that moves a good between two of them; what nobody
  has written is the *deciding* — the orders and the silo, later in M5.
- **Structures have no behavior** beyond holding what is put in them, and there
  is one generic kind; the roster is M5's (silo) and M6's (cleaner, mill,
  bakery). They are also the last placed thing still a plain object in
  `WorldGrid`'s registries rather than an entity row.
- **Player interaction is the palette, four tools, the time bar and the field
  panel**, plus three dev keys — nothing hires. The unlock *seam* exists
  (`ToolAvailability`) and none of the rules — M8's; the HUD pass is M10's.
- **Nothing puts money in.** Wages take it out daily (`## Labour and wages`);
  the credit and refund seams exist, pay nothing, and are M7's.
- **Nothing stops a road being bulldozed out from under a machine** driving it,
  and nothing re-checks a building's road access when the road beside it goes.
  Both are M5 cases; the notes sit on `WorldGrid.Clear`, where M5 will be.
