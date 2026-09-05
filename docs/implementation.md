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

- **Godot 4.7 stable (.NET/mono build)**, C# on **.NET SDK 8**. Renderer Forward
  Plus on D3D12, physics Jolt (both set in `project.godot`).
- `dotnet build Arable.sln` — the `.csproj`/`.sln` are hand-written because
  Godot's `--build-solutions` will not create them from scratch
  (`Godot.NET.Sdk/4.7.0`, `net8.0`, nullable enabled).
- Run with `godot --path .`. Main scene is
  `scenes/Main.tscn`.

## Project layout

```
assets/dev/        tile_library.tres — the GridMap MeshLibrary
assets/icons/      toolbar/ — build-tool icons (white SVGs, tinted per state)
docs/              design docs (concept, tech decisions, this file)
scenes/            Main.tscn is the entry point; world/ holds instanced
                   scenes; dev/ holds test scenes, not part of the game
src/camera/        CameraRig.cs
src/ui/            BuildPalette, DevShortcuts, CellPicker, CellInspector
src/ui/build/      BuildTool (the base) + Road/Field/Structure/Bulldoze tools
src/world/         WorldGrid, TileType, PlacementRules, Field, Structure,
                   Removal, Economy, Machine
src/dev/           the scripts behind scenes/dev
```

Namespace is `Arable` throughout. `.godot/` is generated cache — never edited or
committed.

**Gotcha (hand-written .tscn):** a Node-typed export serialized as
`World = NodePath("../World")` only resolves if the `[node]` header also carries
`node_paths=PackedStringArray("World")`. Without that marker the property loads
as null.

## Camera (`src/camera/CameraRig.cs`)

RTS-style isometric camera, per the tech.md decision: orthographic `Camera3D`,
fixed pitch, pan + zoom + 90°-step rotation.

**Structure.** The `CameraRig` node sits on the ground plane and only ever yaws;
its child `Camera3D` holds everything else fixed — true-isometric pitch
(35.264° = atan(1/√2)), orthographic projection, a distance offset. The rig
starts at 45° yaw, so 90° steps always keep the classic iso diamond.

**Controls** (actions in the `[input]` section of `project.godot`): WASD/arrows
pan relative to yaw, middle-drag pans 1:1, wheel zooms (×1.15/step, clamped
6–80), Q/E rotate in 90° steps.

**The three things to know before changing it:**

- Zoom changes the camera's orthographic `Size`, not its position, and keyboard
  pan speed scales with zoom so screen-space speed feels constant.
- Drag pan converts pixels to world units via `Size / viewport height` and
  divides the vertical component by `sin(pitch)` to undo the iso foreshortening.
  That division is what makes the ground stick to the cursor.
- Zoom and rotation smooth with frame-rate-independent exponential decay
  (`1 - exp(-k·dt)`), and the rig tracks *unwrapped* target yaw, so repeated
  rotations accumulate instead of fighting each other.

All tunables are `[Export]`ed.

## World grid (`src/world/`)

Four entity kinds exist: **roads** (machine-traversable), **fields** (workable
land), **structures** (buildings) and **machines** (vehicles). Roads are plain
grid tiles; a field and a structure are each a tile layer *plus* an entity that
owns those cells; machines are moving scene entities — deliberately *not* grid
cells.

**Data/view split.** `WorldGrid` (a `Node3D` named `World` in Main.tscn) owns
the logical world state; its child `GridMap` is presentation only. Game logic
goes through `WorldGrid` and must **never read the `GridMap` back** — every
mutator keeps the view in sync. This is the first step toward tech.md's rule
that the sim is decoupled from rendering.

**Two layers, stored separately.** What the land *is* and what the player
*built* are different things, and they never share storage:

- **terrain** — `TerrainType` (`OutOfBounds | Soil | Rock | Water`) plus a
  `float` fertility per cell. Generated from the seed; the player never edits it.
- **placement** — `TileType` (`Empty | Road | Field | Structure`). The player
  owns it, and clearing a cell back to `Empty` leaves the terrain underneath
  untouched. `Field`/`Structure` say only *that* something is placed — *which*
  one is a question for the entity registries below.

That separation is what makes bulldozing lossless. `GetTerrain` answers
`OutOfBounds` off the map rather than throwing, so a caller can tell "not on the
map" from any real terrain in one call.

**Terrain generation** (`GenerateTerrain`, run before the start road):

- The map is **bounded**: −`MapHalfExtent`..`MapHalfExtent` on both axes, 48 by
  default → 97×97 cells = 194 m at 2 m cells. That fits inside Main.tscn's
  200×200 ground plane and is slightly larger than maximum zoom-out shows.
- **One seed** drives everything: a fertility noise and a rock/water mask
  (`WorldSeed + 7919`), both SimplexSmooth + FBM. The mask reads as a coarse
  elevation — below `WaterLevel` is water, above `RockLevel` is rock, the rest
  soil — so, deliberately, water and rock never border each other. Defaults give
  roughly 76 % soil / 14 % water / 10 % rock.
- Fertility is **0 on rock and water**: it means "how good is this soil", and
  those cells have none.
- Storage is **flat arrays** indexed by cell, not a dictionary — every in-bounds
  cell has a value, and this is the first piece of state laid out the way the M3
  sim core wants all of it. The extent the arrays were built with is cached, so
  changing `MapHalfExtent` at runtime can never index past them.
- It is **deterministic and re-runnable**: same seed in, bit-exact same arrays
  out, and it never touches the placement layer — so regenerating leaves roads
  and fields exactly where they were.
- The starting road strip (z = 0, x = −16..16) is **carved** to soil rather than
  biasing the noise, so the seed still owns every other cell.

**How the two layers render.** One `GridMap` draws both: `ViewItem(cell)`
returns the placed tile's mesh item when the cell has one and the terrain's
otherwise, so placement *hides* terrain visually without overwriting it, and
clearing brings the same terrain back. Cells are 2 m with the tile origin on the
ground plane; conversion goes through `CellToWorld`/`WorldToCell`. Dev art is
`assets/dev/tile_library.tres`, where soil has **four fertility shades** so the
fertility field is legible in the iso view.

**Road-network queries live here**, because they are questions about the grid
rather than about a vehicle:

- `FindRoadPath` — BFS shortest path over road cells, 8-neighbour, where a
  diagonal step is allowed only past a road *corner* (at least one of the two
  cells sharing that corner is road). That rule is what stops a path squeezing
  through the point gap between two unconnected road strips.
- `SmoothRoadPath` — string pulling: drops every waypoint the vehicle can skip
  by driving straight, tested with `HasRoadLineOfSight`, an exact integer
  supercover walk of the crossed cells under the same corner rule.
- `LineCells` — Bresenham, except that a diagonal step stair-steps through two
  orthogonal cells so the road footprint stays 4-connected, and the connector
  cell **alternates sides** on successive diagonals so the staircase stays
  centred on the true line. Without the alternation, machines — which drive
  center-to-center — would hug one edge of the band.
- `RectCells` — the filled inclusive rectangle between two opposite corners,
  row-major from the minimum, so the list depends on the rectangle and not on
  which corner the drag started from.

**Mutators.** `SetTile` is the single write path, and it maintains both entity
registries so that no caller has to remember to:

| Mutator | Does |
|---|---|
| `MarkField(cells)` | creates **one** `Field`, stamps tiles, indexes cell → field |
| `PlaceStructure(cells)` | the twin: **one** `Structure`, tiles, cell → structure |
| `BuildRoadLine(from, to)` | stamps the `LineCells` road |
| `Clear(cell)` | the one removal path; returns a `Removal` describing what came off, or **null** when the cell held nothing |

The three placing mutators are the **unvalidated** entry points, for start
layout, dev keys and tests: they overwrite whatever held the cells rather than
refusing. Anything the *player* places goes through a `BuildTool`, and therefore
through `PlacementRules`.

Two asymmetries in `SetTile` that everything downstream inherits: writing over a
field cell detaches **just that cell**, and a field that loses its last cell is
dropped; writing over **any** cell of a structure demolishes the whole building.
`Clear` returning null is load-bearing for the second — it is what stops one
building being refunded once per cell a drag clipped.

**Start layout** (`GenerateStartRoad`, deterministic): one straight road along x
through the origin, on the soil strip generation carved for it. Nothing else —
every other road, field and building comes from build actions.

**Open, from M5:** a road cell can be cleared out from under a machine driving
it, or one that has it in an already-computed route. Nothing prevents or
re-validates that; the note lives on `WorldGrid.Clear`, where whoever writes M5's
vehicles will be standing.

## Machines (`src/world/Machine.cs`, `scenes/world/Machine.tscn`)

A machine is any vehicle — tractor, combine, truck. One generic scene/script for
now, varied per instance by exported `Speed`, `TurnSpeed`, `BodyColor`.

- **Dev model:** box body, white cab, four cylinder wheels; forward is **−Z**.
  The meshes sit under a `Model` child that carries the visual scale, so the
  root's transform stays purely sim state (position + yaw) and resizing the art
  never touches movement code.
- **Behavior:** wander the road network — pick a random road cell, `FindRoadPath`
  to it, run that through `SmoothRoadPath`, drive the waypoints, repeat. The
  smoothing is what keeps driving on a stair-stepped diagonal road from
  zigzagging. A machine that finds itself off-road warns and parks.
- **Movement** runs in `_PhysicsProcess` (fixed 60 Hz, per tech.md). Each tick
  spends a travel budget (`Speed·dt`) across waypoints so corners do not lose
  distance; yaw turns smoothly toward the travel direction.
- **Determinism:** each machine gets a seeded `System.Random` from `WorldGrid`,
  and spawn positions come from a fixed-seed counter, so any spawn sequence is
  reproducible.
- `WorldGrid.SpawnMachine()` spawns one at a random road cell; machines join the
  `machines` group. `MachineCount` defaults to **0** — the game starts with no
  machines, spawn them with dev key 9.

## Build palette (`src/ui/BuildPalette.cs`)

> **The toolbar is how a build tool is chosen.** One button per tool along the
> bottom, showing what it does, what it costs and the key that arms it, with the
> armed one filled in the build cursor's amber — so what the next click will do
> is legible without moving the mouse.

The bar is built **in code** from the exported `Tools` list, in bar order, so
the order is a design decision rather than an accident of tree order. Putting
M5's silo on the bar is adding it to that list.

**Each button reads everything off the tool** — `DisplayName`, `Icon` and
`CostPerCell` are `[Export]`s on `BuildTool` — so the bar can never quote a
number the click does not charge, and repricing or re-picturing a tool in the
editor moves the button. A tool with no icon falls back to its name, so a
half-wired scene degrades instead of showing a blank square. Price wording
follows how the tool charges: `5 / cell` for a drag tool, `250` for a
single-click one, `free` rather than a zero, grouped with `InvariantCulture` so
the bar and the balance agree on every machine.

**It is not a second source of truth.** Which tool is armed is a fact about the
tools (`BuildTool.Active`, kept unique by the `build_tools` group); the palette
only ever reads it. `Refresh()` repaints every button from the tools' own state
every frame, so a tool disarmed by *any* other route — Esc, right click, another
tool arming, a dev script, a screenshot run — is right on the bar the same
frame. There is no palette-side copy that could disagree.

**Keys are an accelerator for the bar, not a way around it.** Key *n* arms the
n-th entry (**1** road, **2** field, **3** structure, **4** bulldoze) by calling
the same `Toggle` a button press calls. The palette claims `menu_1` upwards, one
key per entry, so the range grows with the roster. `Select` is public because
`BuildSmokeTest` arms tools along the path a click takes, without synthesizing
mouse events on a `Control`.

**The M8 seam is `ToolAvailability`.** Every entry is `Available`, `Locked`
(greyed and unclickable, still on the bar) or `Hidden` (off the bar). A
non-available entry is refused on **every** path in — button, key and `Select`
alike — because a seam only one path respects is decoration; and a tool that
stops being available while the player holds it is disarmed on the spot, so the
bar never says "locked" about the tool the clicks are going to. The *rules* are
M8's: nothing here knows what an unlock is, and nothing sets an entry to
anything but `Available` today.

**Two gotchas in the look.** Icons are tinted with a *modulate*, which
multiplies, so **the source artwork has to be white** — a dark glyph would stay
dark whatever colour it was given. And the root `Control` is
`MOUSE_FILTER_IGNORE`, as are the inner labels: a full-screen `Control` would
otherwise swallow every click meant for the world. The bar is anchored
bottom-centre and grown from its own centre, so it stays clear of the cell
readout (top-left) and the money readout (top-right) at any window size.

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
tool gets written.

**What a subclass supplies** (the whole contract):

| Member | Meaning |
|---|---|
| `TileType PlacedTile` | what the tool writes |
| `PlacementRule Rules` | the legality rules it opts into |
| `Footprint(anchor, cell)` | cells a drag covers (`LineCells`, `RectCells`, or one cell) |
| `Apply(PlacementPlan)` *(virtual)* | writes the placement; the default stamps `PlacedTile` over every cell |
| `NeedsAnchor` *(virtual, true)* | false for single-click tools, which ghost as soon as the cursor moves |
| `Policy` *(virtual, `EveryCell`)* | how per-cell verdicts add up over a footprint |
| `GhostColorFor(plan, i)` *(virtual)* | how the ghost paints cell `i` |

**The four tools** (`CostPerCell` is exported; every number is a placeholder):

| Tool | Key | Places | Costs | Rules | Footprint |
|---|---|---|---|---|---|
| `RoadBuildTool` | **1** | `Road` | 5/cell | `BuildableTerrain` + `NoOverlap` | `LineCells` |
| `FieldBuildTool` | **2** | `Field` | 10/cell | `BuildableTerrain` + `VacantCell` | `RectCells` |
| `StructureBuildTool` | **3** | `Structure` | 250 | `BuildableTerrain` + `VacantCell` + `TouchesRoad` | the hovered cell |
| `BulldozeTool` | **4** | *removes* | 0 | `InBounds` + `OccupiedCell`, `Policy` = `AnyCell` | `RectCells` |

Fields and structures override `Apply` to call `MarkField`/`PlaceStructure`,
because the default would write tiles and leave farmland or a building that
nothing could address. `BulldozeTool` is the one subclass that bends the base
rather than filling in the contract — see **Bulldozing**.

**The rules** (`PlacementRule`, a `[Flags]` set — add a flag rather than
re-coding a check inside a tool). Each exists for a reason that is not
interchangeable with the others:

- `BuildableTerrain` — soil only; rock, water and off-map are refused (`OffMap`
  is a separate refusal so the message can differ).
- `NoOverlap` — the cell must not hold a *different* placement. Placing what is
  already there stays legal, which is what lets a road branch off the network.
- `VacantCell` — the cell must hold **nothing at all**. Stricter than
  `NoOverlap`, and the difference is exactly the "already there" case: over a
  field tile `NoOverlap` would allow a second placement, `VacantCell` refuses.
  Fields need it because a cell belongs to exactly one `Field`, so a second
  rectangle over the first would leave two entities claiming the same ground.
- `InBounds` — on the map, and nothing else. `BuildableTerrain` is its stricter
  form, so a tool sets one or the other. It exists for the bulldozer, whose
  business the terrain kind is not: what may be *taken off* a cell has nothing
  to do with what could be built there.
- `OccupiedCell` — the cell must hold **something**; the exact inverse of
  `VacantCell`, refusing with `NothingToClear`. Mutually exclusive with
  `VacantCell`/`NoOverlap`: a tool wants the cell free or wants it taken, never
  both.
- `TouchesRoad` — a **footprint-level** rule: some cell must share an edge with
  a road cell *outside* the footprint, so a placement can never satisfy its own
  road requirement. 4-neighbour — a road meeting only a corner is not access.
  It is what makes the road network load-bearing rather than decorative. Because
  it is footprint-level, a refusal blames no individual cell and the ghost dims
  the whole placement.

`PlacementRules.Check(world, cells, rules, placing, policy, budget)` returns a
`PlacementPlan`: the cells, per-cell refusals, the one refusal describing the
whole placement, and what committing it would cost. `Explain(refusal)` gives the
player-facing wording. How verdicts add up is the `FootprintPolicy`, and there
are exactly two — because building and clearing genuinely want opposite answers
about a mixed region, not because a tool might prefer one:

- `EveryCell` (every building tool): **partial legality is all-or-nothing** —
  one illegal cell refuses the whole drag, and nothing lands on "the legal part"
  of it. A half road, or a field with a bite out of it, is not what the player
  asked for. The per-cell verdicts exist only so the ghost can point at the
  offenders.
- `AnyCell` (the bulldozer, and only it): one passing cell is enough and the
  failing ones are skipped. It is also what a placement is *priced* on — an
  `AnyCell` drag pays for the cells it acts on, not the ground it crossed.

**What the player sees.** The blinking hover square is yellow or red by the
verdict under the cursor. Once anchored, a `MultiMesh` ghosts the footprint with
per-instance colours: legal-yellow when the whole placement is allowed,
otherwise the offending cells marked and the rest dimmed — a refused drag never
shows a legal-coloured cell, because none of it is going to be built. The one
thing the ghost promises is that **a legal-coloured cell is a cell something
will happen to**, which is why `GhostColorFor` is virtual: under `AnyCell` a
legal plan still contains cells it will skip, and the bulldozer dims those.

**Interaction.** The first left click anchors (and is itself validated), the
second places and re-arms. A refused click writes nothing *and keeps the
anchor*, so the player just re-aims. Right click / Esc drops the anchor first
and leaves the tool second. `NeedsAnchor = false` skips the anchor entirely:
ghost on arm, place on the first click.

**One armed tool at a time.** Every tool joins the `build_tools` group in
`_Ready`, and `SetActive(true)` disarms every other member — so two tools can
never listen to the same click, and a disarmed tool drops its anchor, ghost and
preview on the way out. This lives in the base rather than in the palette on
purpose: a new tool gets it for free, and the palette has to know neither the
list nor the rule.

**Driving a tool without a cursor.** Every state-changing entry point is public
and cell-driven (`SetActive`/`Toggle`, `HoverAt`, `ClickCell`, `Cancel`,
`PlanFor`), and the preview reads back through `Preview`, `PreviewLegal`,
`GhostCellCount`, `GhostColor(i)`, `CursorColor`. That is how the headless tests
assert what the player would see; see **Dev smoke tests** for the two traps.

### Fields (`src/world/Field.cs`, `src/ui/build/FieldBuildTool.cs`)

> **Farmland is addressed by one `Field` entity per marked rectangle — never by
> the cell.** One drag creates exactly one `Field`. The cells get
> `TileType.Field` only so the `GridMap` can draw them; nothing in the game
> refers to "that field cell". Crops, jobs, yields and M4's output buffer hang
> off the entity, reached with `WorldGrid.GetField(cell)`.

`Field` holds `Id` (creation order, never reused), `Name` (defaulted, settable,
no rename UI yet), its `Cells`, `CellCount`, `Contains` and `Bounds`.

What that choice commits us to — all of it deliberate:

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
- **Road access is deliberately not required** to mark a field. Nothing works a
  field until machines get jobs in M4, and that is when the rule (if any)
  belongs.

### Structures (`src/world/Structure.cs`, `src/ui/build/StructureBuildTool.cs`)

> **A building is addressed by one `Structure` entity — never by the cell.** The
> cells get `TileType.Structure` only so the `GridMap` can draw them and
> `PlacementRules` can call them occupied. Reach the building with
> `WorldGrid.GetStructure(cell)` or `GetStructure(id)`.

One generic placeholder building today; the roster — silo in M5, cleaner, mill
and bakery in M6 — hangs off the same tool. What exists is placement and the
adjacency rule. It is the first single-click tool (`NeedsAnchor = false`).

**Why an entity and not just a tile value.** `TileType.Structure` can say that
*a* building is here; it cannot say *which*, and a building needs an identity
long before it needs behaviour. M5 sends a vehicle to *a silo*; M6 hangs a
recipe and buffers off *that* mill and not the one next to it; a save file has
to name it. `Id` is that handle — creation order, never reused, resolving to
null once the building is gone, so an order pointing at a demolished mill fails
loudly rather than hitting whatever was built there since. Bolting the entity on
later would have meant migrating every cell already stamped as a bare tile.

**How a 2×2 lands on this without a rewrite.** A structure owns a *list* of
cells even though the tool hands it one, and everything downstream of the
footprint is already per cell: `Check` iterates cells, `TouchesRoad` already
excludes the placement's own cells, the ghost draws a cell per plan entry, the
registry indexes cell → structure, demolition walks the footprint. The one line
that changes is the tool's `Footprint`.

**Where a building parts company with a field** — the one deliberate asymmetry:
a building is **atomic**. A field shrinks cell by cell; clearing *any* cell of a
building demolishes the whole thing, because half a mill is not a mill.
`SetTile` does it, detaching the whole footprint from the registry *before*
writing any tile so the clearing writes cannot re-enter it. With 1×1 footprints
the two rules are indistinguishable — the difference is written now because it
is the multi-tile question, not later when four cells make it urgent.

Road access is checked at *placement* only; nothing re-checks it when the player
bulldozes the road away afterwards. Cut-off buildings are M5's problem, when
there is delivery to fail.

### Bulldozing (`src/ui/build/BulldozeTool.cs`, `src/world/Removal.cs`)

> **Anything the player placed can be taken back off, and the terrain under it
> is never touched** — the ground comes back exactly as it was, fertility and
> all, because the two layers were never stored together.

**Legality is inverted, so it gets its own rules.** None of the building rules
describe a removal: `BuildableTerrain` asks whether soil could be built on, and
`VacantCell` demands the exact opposite of what this tool is for. Bending either
into shape would have made both rules mean two things. So the bulldozer opts
into `InBounds` + `OccupiedCell` and there is no third rule: **what a cell holds
never makes it un-removable.** The only two refusals are the map edge and an
empty cell. Bare rock is refused for holding nothing, *not* for being rock.

**A mixed drag clears what is there and skips what is not.** Building is
all-or-nothing; for removal that would be unusable — clearing a farmyard means
dragging over the gaps between its buildings, and one empty cell would refuse
the lot. Hence the sole use of `AnyCell`. The ghost keeps it honest by drawing
only the cells that will actually be cleared in the legal colour. The same rule
applies to the *anchoring* click, so a drag still has to start on something
removable — a deliberate consequence, and the first piece to revisit if
bulldozing ever feels fiddly in play.

**Both registry rules reach the player through this one tool** and they differ:
bite a field and what is left is still that field under the same name; clip any
cell of a building and the whole building goes. The tool implements neither —
every cell goes through `WorldGrid.Clear`, where both rules already live.

**The refund seam is an M7 stub.** Every removal passes through exactly one
function, `RefundFor(Removal)`, and `Apply` credits what it returns to the
`Economy` — so the day M7 puts a fraction of a build cost in that expression is
the day refunds appear in the balance, with nothing else touched. It pays
**nothing** today, deliberately rather than unfinished: the refund *economics*
are M7's to design. What the seam already carries is everything a real rule
needs — `Removal` names the tile kind, the entity (kept as the object, so a
building can be priced by *what* it is once the roster exists), the cells
actually freed, and the cell the player hit. `Removals` is the ledger, **one
entry per removal, not per cleared cell**, which is why clipping two cells of
the same building refunds it once.

### Money and build costs (`src/world/Economy.cs`)

> **Building costs money, and "you cannot afford this" is a placement refusal
> like any other** — decided with the rest of the verdict, so the ghost shows it
> before the click instead of the click discovering it. Money itself is a **stub
> number** until M7 gives it a market: this is the plumbing, not the economy.

`Economy` is a `Node` in Main.tscn and **the one thing that owns the balance**,
so there is one number, one place it changes, and one thing to serialize when
saves arrive. `TrySpend` takes the money **or refuses and changes nothing**, so
the balance can never go negative however a caller is written; `Credit` ignores
non-positive amounts, so the M7 refund of 0 is a genuine no-op; `SetBalance` is
the seam a save-load, or a test that wants the player broke, uses.

**Cost is a validation input, not a post-hoc check.** A tool's `CostPerCell` and
the current balance go into `PlacementRules.Check` as a `PlacementBudget` — a
*value*, never a handle on the account, which keeps the evaluator the pure,
node-state-free thing it was. `Check` prices the placement and refuses it with
`CannotAfford` when the total is out of reach; everything downstream then works
unchanged. Testing affordability inside `ClickCell` instead would have made the
ghost lie — showing a placement as legal that the click then refused — which is
the whole reason the price lives in the plan.

Like `TouchesRoad`, affordability is a property of the **whole placement**: the
player buys all of a ten-cell road or none of it, never "the cells that were
individually affordable". No single cell is the offender, so the ghost dims the
placement and blames nobody. It is judged **last**, after every reason that is
not about money, so a drag into water is refused for the water.

**Priced for the cells it acts on**: under `EveryCell` the whole footprint,
under `AnyCell` only the cells that pass — charging a bulldoze for ground it
merely crossed would be charging for nothing.

**Charged on commit, never on preview.** `ClickCell` spends `plan.Cost` on the
committing click, and nothing else in the tool touches money. However long a
drag is held and however often the ghost is recomputed, nothing moves. A refused
click is free.

**The knobs are exported and wired in Main.tscn** (`StartingBalance`, and
`CostPerCell` per tool), so tuning is an editor change, not a rebuild. Each tool
carries the same placeholder in its constructor so a code-built tool is priced
too; the scene value wins. A tool with **no** `Economy` wired builds for free —
that is "there is no money in this scene", not "the player is broke", and it is
what keeps a dev scene or a rules-only test working.

**Out of scope, deliberately**: earning money (M5's depot), prices moving (M7),
wages (M5). Nothing yet puts money *in* except a refund of zero.

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
thinks it is: a screen-corner `Label` naming the cell under the cursor —
coordinates, terrain, fertility, placed tile, and the owning `Field`/`Structure`
by name where there is one, because the entity is what the game addresses
farmland and buildings by.

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
godot --headless --path . res://scenes/dev/CameraSmokeTest.tscn  # rig pans, rotates, zooms
godot --headless --path . res://scenes/dev/WorldSmokeTest.tscn   # terrain, seed determinism, layer
                                                                 # independence, machines, and the
                                                                 # picker/inspector pixel round trip
godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn   # palette, rules, ghost, all four
                                                                 # tools, costs, the refund seam
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

The views cover the default pose, each build ghost in both verdicts, a field and
a building being placed, the bulldozer's mixed drag, rotation, zoom, an overview
camera and the hover readout. Each `PASS` line captions what the frame is meant
to show — the caption, not the pixel colour, is what says which cell killed a
drag. Conventions worth keeping: views settle by **time**, not frame count (the
rig smooths on `delta`, so a frame count converges differently on a fast
machine); anything cursor-driven is pinned with `SetProcess(false)` and fed an
explicit cell, since a screenshot run has no mouse; and legal spots are
**searched through the tool**, not hard-coded, for the same reason the smoke
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
  already takes any footprint. Nothing re-checks road access when the road
  beside a building is bulldozed away.
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
  driving it or has it in a route. A real case from M5, not an impossible one.
- **The simulation still lives in Godot nodes**; the standalone deterministic
  sim core (ECS-like layout, save/replay) comes when there is real sim state to
  own.
- **Flow fields**: BFS per machine is fine at this scale; revisit when mover
  count grows.
