# Implementation notes

What exists in the codebase today and how it fits together. Companion to
`tech.md` (the decisions) — this documents how those decisions were realized.
Last updated: 2026-09-05.

## Building & running

- Engine: **Godot 4.7 stable (.NET/mono build)**, C# on **.NET SDK 8**.
- `dotnet build Arable.sln` compiles the game assembly (the `.csproj`/`.sln`
  were written by hand because Godot's `--build-solutions` won't create them
  from scratch; SDK is `Godot.NET.Sdk/4.7.0`, `net8.0`, nullable enabled).
- Run the game: open the project in the Godot editor and press F5, or
  `godot --path . ` from the CLI. Main scene is `scenes/Main.tscn`.
- Renderer: Forward Plus on D3D12; physics: Jolt (both set in `project.godot`).

## Project layout

```
assets/dev/        dev-art resources (tile_library.tres — GridMap MeshLibrary)
docs/              design docs (concept, tech decisions, this file)
scenes/            .tscn scenes; Main.tscn is the entry point
scenes/world/      scenes instanced by the world (Machine.tscn)
scenes/dev/        headless smoke-test scenes (not part of the game)
src/camera/        CameraRig.cs
src/ui/            BuildPalette.cs (the build toolbar), DevShortcuts.cs (dev keys),
                   CellPicker.cs (screen -> cell), CellInspector.cs (hover readout)
src/ui/build/      BuildTool.cs (base for every placement tool), RoadBuildTool.cs,
                   FieldBuildTool.cs, StructureBuildTool.cs, BulldozeTool.cs
src/world/         TileType.cs, WorldGrid.cs, PlacementRules.cs, Field.cs,
                   Structure.cs, Removal.cs, Economy.cs (the player's money),
                   Machine.cs
src/dev/           smoke-test scripts backing scenes/dev
```

Namespace is `Arable` throughout. `.godot/` is generated cache — never edited
or committed.

## Camera (`src/camera/CameraRig.cs`)

RTS-style isometric camera, per the tech.md decision (orthographic `Camera3D`,
fixed pitch, pan + zoom + 90°-step rotation).

**Structure.** The `CameraRig` node sits on the ground plane and only ever
yaws; its child `Camera3D` holds everything else fixed: true-isometric pitch
(35.264° = atan(1/√2)), orthographic projection, and a distance offset. The rig
starts at 45° yaw, so with 90° steps the view always keeps the classic iso
diamond orientation.

**Controls** (actions defined in the `[input]` section of `project.godot`):

| Action | Keys | Effect |
|---|---|---|
| `camera_forward/back/left/right` | WASD / arrows | pan in the ground plane, relative to current yaw |
| `camera_drag` | middle mouse (drag) | pan 1:1 — the ground point under the cursor follows the mouse |
| `camera_zoom_in/out` | mouse wheel | multiplicative zoom (×1.15/step), clamped 6–80 |
| `camera_rotate_left/right` | Q / E | rotate in 90° steps |

**Implementation notes**

- Zoom changes the camera's orthographic `Size` (not its position).
- Keyboard pan speed scales with zoom so screen-space speed feels constant.
- Zoom and rotation are smoothed with frame-rate-independent exponential decay
  (`1 - exp(-k·dt)`); the rig tracks unwrapped target yaw so repeated rotations
  accumulate correctly.
- Drag pan converts pixels to world units via `Size / viewport height`, and
  divides the vertical component by `sin(pitch)` to undo the iso foreshortening
  — that's what makes the ground "stick" to the cursor.
- All tunables (`PanSpeed`, `ZoomMin/Max`, smoothing rates, …) are `[Export]`ed.

## World grid (`src/world/`)

Four entity kinds exist: **roads** (machine-traversable), **fields** (workable
land), **structures** (placed buildings), and **machines** (vehicles that move
around the map). Roads are plain grid tiles; a field and a structure are each a
tile layer *plus* an entity that owns those cells (see **Fields** and
**Structures** at the end of the build-tools section — those are the units
farmland and buildings are addressed by); machines are moving scene entities —
deliberately *not* grid cells.

**Data/view split.** `WorldGrid` (a `Node3D` named `World` in Main.tscn) owns
the logical world state; its child `GridMap` is presentation only. Game logic
must go through `WorldGrid` (`GetTile`/`SetTile`/`GetTerrain`/`IsRoad`/…) and
never read the `GridMap` back — every mutator keeps the view in sync. This is
the first step toward the tech.md rule that the sim is decoupled from
rendering.

**Two layers, stored separately.** What the land *is* and what the player
*built* are different things and never share storage:

- **terrain** — `TerrainType` (`OutOfBounds | Soil | Rock | Water`) plus a
  `float` fertility per cell. Generated from the world seed; the player never
  edits it. Read with `GetTerrain(cell)` / `GetFertility(cell)` / `IsSoil(cell)`.
- **placement** — `TileType` (`Empty | Road | Field | Structure`). The player
  owns it; clearing a cell back to `Empty` leaves the terrain underneath
  untouched. `Field` and `Structure` say only *that* something is placed —
  *which* field or building is a question for the entity registries below.

`TerrainType.OutOfBounds` is the value `GetTerrain` returns off the map, so a
caller can tell "not on the map" from any real terrain in one call;
`InBounds(cell)` answers the same question directly and `GetFertility` returns
0 out of bounds.

**Terrain generation** (`GenerateTerrain`, called from `_Ready` before the
start road):

- The map is **bounded**: cells −`MapHalfExtent`..`MapHalfExtent` on both axes.
  `MapHalfExtent = 48` → a 97×97 grid = 194 m across at 2 m cells, which fits
  inside Main.tscn's 200×200 ground plane and is a little larger than what the
  camera shows at maximum zoom-out.
- **One seed** (`WorldSeed`, exported) drives everything. Two `FastNoiseLite`
  fields derive from it: fertility (`Seed = WorldSeed`) and a rock/water mask
  (`Seed = WorldSeed + 7919`), both SimplexSmooth + FBM. The mask reads like a
  coarse elevation — below `WaterLevel` becomes water, above `RockLevel`
  becomes rock, the rest soil — so, deliberately, water and rock never border
  each other. Defaults give roughly 76 % soil / 14 % water / 10 % rock.
- Fertility is the fertility noise remapped to 0..1, and is **0 on rock and
  water**: it means "how good is this soil", and those cells have none.
- Storage is **flat arrays** indexed by cell (`TerrainType[]`, `float[]`), not
  a dictionary — every in-bounds cell has a value, and this is the first piece
  of state laid out the way the M3 sim core wants all of it. The extent the
  arrays were built with is cached in `_halfExtent`, so `MapHalfExtent`
  changing at runtime can never index past the arrays.
- `GenerateTerrain` is **deterministic and re-runnable**: same seed in, same
  arrays out (bit-exact fertility), and it never touches the placement layer,
  so regenerating leaves roads and fields where they were.
- The starting road is **carved**: generation forces its strip (z = 0,
  x = −16..16) to soil rather than biasing the noise, so the seed still owns
  every other cell.

**How the two layers render.** One `GridMap` draws both. `ViewItem(cell)`
returns the placed tile's mesh item when the cell has one and the terrain's
otherwise, so placement *hides* terrain visually without overwriting it, and
clearing the tile brings the same terrain back. `SetTile` writes data then
calls `RefreshCell`; `GenerateTerrain` ends with `RedrawAllCells`.

- Grid cells are **2 m** (`cell_size = (2, 1, 2)`, `cell_center_y = false` so
  tile origin is the ground plane). Cell↔world conversion goes through
  `CellToWorld`/`WorldToCell`.
- Road-network queries live here too: `FindRoadPath` (breadth-first shortest
  path over road cells, 8-neighbor — a diagonal step is allowed only past a
  road corner, i.e. when at least one of the two cells sharing that corner is
  road, so paths never squeeze through the point gap between two unconnected
  road strips), `SmoothRoadPath` (string pulling: drops every waypoint the
  vehicle can skip by driving straight, using `HasRoadLineOfSight` — an exact
  integer supercover walk of the cells a center-to-center segment crosses,
  with the same corner rule), and `RandomRoadCell`.
- Road building: static `LineCells` (Bresenham line between two cells; where
  the line calls for a diagonal step it stair-steps through two orthogonal
  cells instead, so consecutive cells always share an edge and the road
  footprint stays 4-connected; the connector cell alternates sides on
  successive diagonal steps so the staircase stays centered on the true line —
  otherwise machines, which drive the center-to-center segment, would hug one
  edge of the band) and `BuildRoadLine` (sets those cells to road — the
  unvalidated way to lay a road, for dev/scenario code; the player's road tool
  goes through `BuildTool` instead).
  Machines still *drive* such a road diagonally: the corner-cutting rule lets
  both the BFS and the smoothing pass run straight along the staircase.
- Field marking: static `RectCells` (the filled axis-aligned rectangle spanned
  by two opposite corners, both included, row-major from the minimum corner —
  so the cell list depends on the rectangle and not on which corner the drag
  started from) is the field tool's footprint the way `LineCells` is the road
  tool's. The mutator is `MarkField(cells)`: it creates **one** `Field`, stamps
  the tiles and indexes cell → field. Beside it sit `Fields` (every field, in
  creation order), `GetField(cell)` (the cell → field lookup the rest of the
  game goes through) and `FieldCellCount`. `SetTile` keeps both sides in step —
  writing anything over a field cell detaches that cell from its field, and a
  field that has lost its last cell is dropped.
- Structure placing: `PlaceStructure(cells)` is the field mutator's twin — it
  creates **one** `Structure`, stamps the tiles and indexes cell → structure.
  Beside it sit `Structures`, `GetStructure(cell)`, `GetStructure(id)` and
  `StructureCellCount`. `SetTile` keeps this side in step too, but with the
  opposite rule to fields: overwriting *any* cell of a building demolishes the
  whole building (see **Structures** below).
- Clearing: `Clear(cell)` is the one removal path — it takes whatever the
  player placed off the cell (through `SetTile`, so both registries keep their
  own rule) and returns a `Removal` describing what came off, or null when the
  cell held nothing. It reports rather than counts because a removal is not a
  cell: a road or field cell is one cell, a building is its whole footprint.
  That "or null" is load-bearing — clearing a second cell of a building that
  is already gone answers null, which is what stops one building being
  refunded once per cell a drag clipped. See **Bulldozing** below, and the
  note in `Clear` about roads being removed under M5's vehicles.
- **Start layout** (`GenerateStartRoad`, deterministic): a single straight
  road along x through the origin (cells −16..16 at z = 0), on the soil strip
  generation carved for it. Nothing else is placed — fields and further roads
  will come from gameplay/build actions. `SetTile` itself still writes outside
  `MapHalfExtent` (legality is a tool-level concern, see the build tools
  below); such cells are drawn too.
- **Dev tiles** (`assets/dev/tile_library.tres`, a hand-written `MeshLibrary`):
  road = flat gray box (item 0), field = raised brown box (item 1), rock =
  tall gray block (item 2), water = thin dark-blue slab sitting lower than
  soil (item 3), soil in **four fertility shades** (items 4–7, pale straw
  → deep green) so the fertility field is legible in the iso view, and
  structure = a barn-red box 1.4 m tall and inset from the cell (item 8), so a
  building reads as a building from any zoom — twice the height of the tallest
  rock, and the only tile art that is neither flat nor gray-brown. Item ids
  are mirrored as constants in `WorldGrid`; soil picks its item by
  `SoilItemFirst + floor(fertility × 4)`.

## Machines (`src/world/Machine.cs`, `scenes/world/Machine.tscn`)

A machine is any vehicle — tractor, combine, truck. One generic scene/script
for now, varied per instance by exported `Speed`, `TurnSpeed`, `BodyColor`.

- **Dev model:** box body (tinted via a material override created in
  `_Ready`), white cab box at the rear, four dark cylinder wheels. Forward is
  **−Z**. The meshes live under a `Model` child node that carries the visual
  scale (0.75) — the root's transform stays purely sim state (position + yaw),
  so resizing the art never touches movement code.
- **Behavior:** wander the road network — pick a random road cell, get a BFS
  path from `WorldGrid`, run it through `SmoothRoadPath` (so the waypoint list
  is a few long straight runs at any angle, not one turn per cell — this is
  what keeps driving on diagonal roads from zigzagging), drive the waypoints
  (cell centers at road-deck height 0.1 m), repeat. A machine that finds
  itself off-road logs a warning and parks itself.
- **Movement** runs in `_PhysicsProcess` (fixed 60 Hz tick, per tech.md).
  Each tick spends a travel budget (`Speed·dt`) across waypoints so corners
  don't lose distance; yaw turns smoothly toward the travel direction.
- **Determinism:** each machine gets a seeded `System.Random` from `WorldGrid`
  (seeds fixed at spawn), so runs are reproducible.
- `WorldGrid.SpawnMachine()` spawns one machine at a random road cell via the
  `MachineScene` export (position and behavior seeds come from a fixed-seed
  spawn counter, so any spawn sequence is reproducible); machines join the
  `machines` node group. `_Ready` calls it `MachineCount` times, but the
  default is 0 — the game starts with no machines; spawn them via dev key 9.

## Build palette (`src/ui/BuildPalette.cs`)

> **The toolbar is how a build tool is chosen.** One button per tool along the
> bottom of the screen, each naming its tool, what it costs and the key that
> arms it, with the armed one filled in the build cursor's amber — so what the
> next click is going to do is legible without moving the mouse. It replaces
> the number-key menu that stood in for it (`MenuController`, deleted; what was
> not a build tool moved to **Dev shortcuts** below).

The palette is a `Control` under the `Hud` `CanvasLayer` in Main.tscn, and the
bar itself is built in **code** from the exported `Tools` list — the four tools
in bar order, wired in the scene, so the order is a design decision and not an
accident of tree order. Putting M5's silo on the bar is adding it to that list.

Buttons are **square and icon-led**: each shows the tool's picture over its
price, with the accelerator digit in the corner. The tool's name is not printed
— the icon is what the player reads at a glance — so it moves to the button's
tooltip, which names the tool, its price and its key.

Each button reads everything it shows **off the tool**: `DisplayName`, `Icon`
and `CostPerCell` are `[Export]`s on `BuildTool`, so renaming, re-picturing or
repricing a tool in the editor moves the button, and the bar can never quote a
number the click does not charge. A tool with no icon wired falls back to its
name in the icon's place, so a half-wired scene degrades instead of showing a
blank square.

Icons live in `assets/icons/toolbar/` as SVGs and are tinted per state — light
on an idle button, dark on the armed amber, dimmed when locked. The tint is a
*modulate*, which multiplies, so **the source artwork has to be white**: a dark
glyph would stay dark whatever colour it were given. The four icons were
recoloured to white on import for exactly that reason. How the price is worded follows how the tool charges — a drag tool
reads `5 / cell`, a single-click tool `250`, and a tool that costs nothing says
`free` rather than showing a zero. Amounts are grouped with
`InvariantCulture`, like the money readout, so the bar and the balance agree on
every machine.

**It is not a second source of truth.** Which tool is armed is a fact about the
tools — `BuildTool.Active`, kept unique by the `build_tools` group — and the
palette only ever reads it. `Refresh()` repaints every button from the tools'
own state and runs every frame, so a tool disarmed by *any* other route (Esc,
right click, another tool arming, a dev script or a screenshot run calling
`SetActive`) is right on the bar the same frame, and there is no palette-side
copy that could disagree. The one thing the palette owns that the tools do not
know is each entry's availability (below).

**One selection path, and the buttons take it:**

| Member | Meaning |
|---|---|
| `Select(int)` / `Select(BuildTool)` | arm that entry's tool; false, changing nothing, for an index off the bar or an entry that is not available |
| `Toggle(int)` | what a button press does: arm the entry, or put the tool down when it is already the armed one |
| `Deselect()` | leave build mode |
| `ActiveIndex` / `ActiveTool` | which entry is armed — derived from the tools every time it is asked, never cached |
| `Entries` / `Entry(int)` / `Count` / `IndexOf(BuildTool)` | what is on the bar |
| `Bar` | the panel the buttons sit on, so where the toolbar is can be asserted |
| `Refresh()` | repaint from the tools now instead of next frame |

`Select` is what the button's `Pressed` handler calls, what the number-key
accelerator calls, and what `BuildSmokeTest` calls — which is the whole reason
it is public: a headless test arms tools along the path a click takes, without
synthesizing mouse events on a `Control`.

**Keys are an accelerator for the bar, not a way around it.** Key *n* arms the
n-th entry — **1** road, **2** field, **3** structure, **4** bulldoze — by
calling the same `Toggle`, so a button and the world can never disagree about
what is armed. The palette claims `menu_1` upwards, one key per entry, so the
range grows with the roster.

**The M8 seam is `ToolAvailability`.** Every entry carries one of `Available`,
`Locked` (still on the bar, greyed out and unclickable) or `Hidden` (off the bar
altogether), set with `SetAvailability(index | tool, availability)` and read
back with `GetAvailability`. An entry that is not `Available` is refused on
**every** path in — the button, the key and `Select` alike — because a seam only
one path respects is decoration; and a tool that stops being available while the
player is holding it is disarmed on the spot, so the bar never says "locked"
about the tool the clicks are going to. The *rules* are M8's: nothing here knows
what an unlock is, and nothing sets an entry to anything but `Available` today.

**Layout and look.** The bar is anchored bottom-centre — the genre's place for a
build toolbar — 18 px clear of the edge and grown from its own centre, so it
stays centred and clear of the cell readout (top-left) and the money readout
(top-right) at any window size; `BuildSmokeTest` asserts that rectangle the way
it asserts the money readout's. The root `Control` is `MOUSE_FILTER_IGNORE` so
only the bar itself takes the mouse — a full-screen `Control` would otherwise
swallow every click meant for the world — the inner labels ignore it too so
every pixel of a button is the button, and the buttons take no focus, so no
focus ring is ever left behind in a screenshot. The styling is `StyleBoxFlat`es
built in `BuildBar`: a dark rounded panel with a shadow, dark grey buttons, and
the armed one filled in `BuildTool.CursorLegal`'s amber with dark text, because
"which tool is listening to your clicks" should be the same colour on the bar as
it is on the ground.

## Dev shortcuts (`src/ui/DevShortcuts.cs`)

What is left of the number-key menu: **8** toggles the hover readout, **9**
spawns a machine on the road network (`WorldGrid.SpawnMachine()`). Neither is a
build tool — which is why they still need a home — and both sit at the top of
the number row because the palette claims that row from the bottom up. The
readout moved off key 2 for exactly that reason: key 2 is the second tool on the
bar now. Nothing in this node reaches a build tool, which is the point: after
M2 there is one way to choose one, and it is the palette.

## Build tools (`src/ui/build/`, `src/world/PlacementRules.cs`)

Every mouse-driven placement tool sits on one base, `BuildTool` (M2's
foundation): hover highlight, ghost preview, validation that refuses an
illegal placement *before* the click, and the anchor → preview → place →
cancel interaction. `RoadBuildTool` was the first subclass and is 30 lines,
most of them comment — copying it is how the next tool gets written, and
`FieldBuildTool` is that copy plus one override. `BulldozeTool` is the one
subclass that is not a copy of any of them: it removes instead of placing, and
**Bulldozing** below is the account of what that inverts.

**What a subclass supplies** (the whole contract):

| Member | Meaning |
|---|---|
| `TileType PlacedTile` | what the tool writes; `NoOverlap` treats a cell already holding it as a no-op |
| `PlacementRule Rules` | the legality rules this tool opts into |
| `IReadOnlyList<Vector2I> Footprint(anchor, cell)` | cells a drag covers (roads: `WorldGrid.LineCells`, a line; fields: `WorldGrid.RectCells`, a filled rectangle) |
| `void Apply(PlacementPlan)` *(virtual)* | writes the placement; the default stamps `PlacedTile` over every cell |
| `bool NeedsAnchor` *(virtual, true)* | false for tools that place on a single click, which then ghost as soon as the cursor moves |
| `FootprintPolicy Policy` *(virtual, `EveryCell`)* | how the per-cell verdicts add up over a footprint: all-or-nothing for a build, `AnyCell` for the bulldozer |
| `Color GhostColorFor(plan, i)` *(virtual)* | how the ghost paints cell `i`; overridden only where a legal plan does not act on every cell |

**The tools that fill it in** (one row per subclass; `CostPerCell` is the
exported price — see **Money and build costs**, and treat every number as a
placeholder):

| Tool | Key | Places | Costs | Rules | Footprint | `Apply` |
|---|---|---|---|---|---|---|
| `RoadBuildTool` | **1** | `Road` | **5**/cell | `BuildableTerrain` + `NoOverlap` | `LineCells` — a stair-stepped line | default (stamps tiles) |
| `FieldBuildTool` | **2** | `Field` | **10**/cell | `BuildableTerrain` + `VacantCell` | `RectCells` — the filled rectangle between the corners | overridden: `WorldGrid.MarkField`, which creates the `Field` entity as well as the tiles |
| `StructureBuildTool` | **3** | `Structure` | **250** (its footprint is one cell) | `BuildableTerrain` + `VacantCell` + `TouchesRoad` | the hovered cell alone (`NeedsAnchor` = false, so one click places) | overridden: `WorldGrid.PlaceStructure`, which creates the `Structure` entity as well as the tile |
| `BulldozeTool` | **4** | nothing — it *removes* | **0** — taking something off is free | `InBounds` + `OccupiedCell`, and `Policy` = `AnyCell` | `RectCells`, like the field tool | overridden: `WorldGrid.Clear` per cell, each removal through the refund seam |

**The rules** (`PlacementRule`, a `[Flags]` set — add a flag rather than
re-coding a check inside a tool):

- `BuildableTerrain` — soil only; rock, water and off-map cells are refused
  (`OffMap` is a separate refusal so the message can differ).
- `NoOverlap` — the cell must not hold a *different* placement. Placing what
  is already there stays legal, which is what lets a road be branched off the
  existing network.
- `VacantCell` — the cell must hold **nothing at all**. Stricter than
  `NoOverlap`, and the difference is exactly the "already there" case: over a
  field tile, `NoOverlap` would let the field tool place again, `VacantCell`
  refuses. Fields opt into it because a cell belongs to exactly one `Field`
  (below), so a second rectangle covering the first would leave two entities
  claiming the same ground. Either rule refuses with `Occupied`; a tool that
  sets both gets `VacantCell`, the stricter one.
- `InBounds` — the cell must be on the map, and nothing else.
  `BuildableTerrain` is its stricter form (on the map *and* soil), so a tool
  sets one or the other. It exists for the bulldozer, whose business the
  terrain kind is not: the map edge still bounds where the player works, but
  what may be *taken off* a cell has nothing to do with what could be built
  there.
- `OccupiedCell` — the cell must hold **something**: the exact inverse of
  `VacantCell`, refusing with `NothingToClear` ("nothing here to clear"). It
  is the whole of a removal's per-cell legality — a cell is never
  un-bulldozable for what it holds, only for holding nothing. Mutually
  exclusive with `VacantCell`/`NoOverlap`; a tool wants the cell free or wants
  it taken, never both.
- `TouchesRoad` — a *footprint-level* rule: some cell of the placement must
  share an edge with a road cell **outside** the footprint, so a placement can
  never satisfy its own road requirement. Adjacency is 4-neighbour — a road
  meeting only a corner is not access. `StructureBuildTool` is its one user,
  and the reason it exists: it is what makes the road network load-bearing
  rather than decorative. Because it is a footprint rule, a refusal blames no
  individual cell — every per-cell verdict is `None`, so the ghost dims the
  whole placement (`GhostRefused`) instead of marking an offender.

`PlacementRules.Check(world, cells, rules, placing, policy, budget)` returns a
`PlacementPlan`: the cells, a parallel array of per-cell `PlacementRefusal`s,
the one refusal that describes the whole placement, and what committing it
would cost. How those add up is
the `FootprintPolicy`, and there are exactly two — because building and
clearing genuinely want opposite answers about a mixed region, not because a
tool might prefer one:

- `EveryCell` (the default, every building tool): **partial legality is
  all-or-nothing** — one illegal cell refuses the entire drag, and nothing
  lands on "the legal part" of it. A half road, or a field with a bite out of
  it, is not what the player asked for. The per-cell verdicts exist only so
  the ghost can point at the cells that caused the refusal.
- `AnyCell` (the bulldozer, and only it): one passing cell is enough and the
  failing ones are skipped; the drag is refused only when there is nothing in
  it at all to remove. See **Bulldozing** for why that inversion is the right
  answer there and how the ghost keeps it honest. It is also what a placement
  is *priced* on: an `AnyCell` drag pays for the cells it acts on and not for
  the ground it merely crossed (**Money and build costs**).

The `budget` (a `PlacementBudget`) is how **money** gets into that verdict
rather than being tested afterwards — see **Money and build costs** below.

`PlacementRules.Explain(refusal)` gives the player-facing wording, which the
tools log on a refused click and a HUD can reuse.

**What the player sees.** The blinking hover square is yellow
(`BuildTool.CursorLegal`) or red (`CursorRefused`) by the verdict on the
placement under the cursor. Once anchored, a `MultiMesh` of the same squares
ghosts the footprint with **per-instance colours** (`UseColors`, white albedo
with `VertexColorUseAsAlbedo`): `GhostLegal` yellow when the whole placement
is allowed, otherwise `GhostIllegalCell` on the offending cells and
`GhostRefused` on the rest — a refused drag never shows a legal-coloured cell,
because none of it is going to be built. The one thing the ghost promises is
that a legal-coloured cell is a cell something will happen to, which is why
`GhostColorFor` is virtual: under `AnyCell` a legal plan still contains cells
it will skip, and the bulldozer paints those `GhostRefused` instead. Highlights
sit at `Machine.DeckHeight + 0.05` so they never z-fight the road deck.

**Interaction.** First left click anchors (and is itself validated: you cannot
anchor on rock, on a cell a field already owns, or — bulldozing — on a cell
with nothing on it), second left click places and re-arms. A refused click
writes nothing *and keeps the anchor*, so the player just re-aims. Right click / Esc drops the anchor first and leaves the
tool second. A tool with `NeedsAnchor` = false — the structure tool — skips the
anchor entirely: it ghosts the cell under the cursor the moment it is armed and
places on the first click.

**One armed tool at a time.** Every tool joins the `build_tools` scene group
(`BuildTool.ToolGroup`) in `_Ready`, and `SetActive(true)` disarms every other
member of it — so two tools can never listen to the same click, and a disarmed
tool drops its anchor, ghost and preview on the way out. It lives in the base
rather than in the palette on purpose: a new tool gets the behaviour for free,
and the palette has to know neither the full list nor the rule — it *reads* the
result (see **Build palette**) instead of keeping its own copy of it.

**Driving a tool without a cursor.** Every state-changing entry point is
public and cell-driven — `SetActive`/`Toggle`, `HoverAt(cell)`,
`ClickCell(cell)` (returns whether the click was accepted), `Cancel()`,
`PlanFor(cell)` — and the preview can be read back through `Preview`,
`PreviewLegal`, `GhostCellCount`, `GhostColor(i)` and `CursorColor`. That is
how `BuildSmokeTest` asserts what the player would see. `GhostColor` reads the
tints the tool handed to the mesh, not the mesh itself: under `--headless` the
dummy renderer keeps no per-instance colours and `GetInstanceColor` answers
black. `PickCell(screenPosition)` still exposes the shared picker.

Note that a tool's `_Process` re-hovers from the real cursor every frame, so a
headless driver must call `HoverAt` in the same frame as the assertion that
depends on it — or `SetProcess(false)` to pin the hover, the way
`ScreenshotTest` pins the hover readout.

Gotcha (hand-written .tscn): a Node-typed export serialized as
`World = NodePath("../World")` only resolves to the actual node if the
`[node]` header also carries `node_paths=PackedStringArray("World")` —
without that marker the property loads as null.

### Fields (`src/world/Field.cs`, `src/ui/build/FieldBuildTool.cs`)

> **Farmland is addressed by one `Field` entity per marked rectangle — never
> by the cell.** One drag creates exactly one `Field`. The cells it covers get
> `TileType.Field` only so the `GridMap` can draw them; nothing in the game
> refers to "that field cell". Crops, jobs, yields and the output buffer M4
> adds all hang off the entity, reached with `WorldGrid.GetField(cell)`.

`FieldBuildTool` (palette key **2**) is the smallest possible `BuildTool`
subclass: soil only, every cell vacant, a `RectCells` footprint, and an `Apply`
override that calls `WorldGrid.MarkField` — because the default `Apply` would
write the tiles and leave farmland nothing could address. `Field` itself holds
`Id` (creation order, never reused), `Name` (defaulted `"Field N"`, settable —
there is no rename UI yet), the `Cells` it owns, `CellCount`, `Contains`, and
`Bounds` (a `Rect2I`, recomputed per call because fields are small and change
only when the player edits them).

What that choice commits us to — all of it deliberate:

- **Exactly one owner per cell.** `GetField` answers with one field or null,
  which is why the tool opts into `VacantCell`: a rectangle can never be drawn
  over ground another field already holds — not even partly, since partial
  legality is all-or-nothing.
- **Touching fields are never merged.** Two adjacent rectangles stay two
  fields, because each is separately named, worked and harvested. So
  **enlarging a field means marking another one** — there is no grow
  operation, by design.
- **A field owns its cells instead of deriving them from the tile layer**, so
  it survives an irregular shape: the rectangle is how the player *draws* one,
  not what a field is allowed to be. Bulldozing (M2) shrinks a field cell by
  cell, and a field that has lost its last cell is dropped — an empty field is
  not something the player can still address. `WorldGrid.SetTile` maintains
  both sides of that relationship, so no caller has to remember to.
- **Road access is deliberately not required** to mark a field. Nothing works
  a field until machines get jobs in M4, and that is when the rule (if any)
  belongs.

`MarkField` is also the unvalidated entry point for dev and scenario code, the
way `BuildRoadLine` is for roads: it transfers cells away from any field that
held them rather than refusing, so the cell → field map can never disagree
with the tile layer. Player placements go through the tool, and therefore
through `VacantCell`, so that path never comes up in play.

### Structures (`src/world/Structure.cs`, `src/ui/build/StructureBuildTool.cs`)

> **A building is addressed by one `Structure` entity — never by the cell.**
> The cells it covers get `TileType.Structure` only so the `GridMap` can draw
> them and `PlacementRules` can call them occupied. Reach the building itself
> with `WorldGrid.GetStructure(cell)` or `GetStructure(id)`.

`StructureBuildTool` (palette key **3**) places one generic placeholder building;
the roster — silo in M5, cleaner, mill and bakery in M6 — hangs off the same
tool. What exists today is placement and the adjacency rule: free soil
(`VacantCell`), buildable ground, and `TouchesRoad`. `NeedsAnchor` is false, so
it is the first single-click tool: no drag, and the ghost shows the verdict as
soon as the cursor moves. `Structure` holds `Id` (creation order, never
reused), `Name` (defaulted `"Structure N"`), the `Cells` it covers, `Origin`
(the first of them), `CellCount`, `Contains` and `Bounds`.

**Why an entity and not just a tile value.** `TileType.Structure` can say that
*a* building is here; it cannot say *which*, and a building needs an identity
long before it needs behaviour. M5 sends a vehicle to *a silo*; M6 hangs a
recipe, an input buffer and an output buffer off *that* mill and not the one
next to it; a save file has to name it. All of that wants a stable handle, and
`Id` is it — handed out in creation order, never reused, and resolving to null
once the building is gone, so an order pointing at a demolished mill fails
loudly rather than hitting whatever was built there since. Bolting the entity
on later would have meant migrating every cell already stamped as a bare tile —
the rewrite this shape exists to avoid.

**How a 2x2 lands on this without a rewrite.** A structure owns a *list* of
cells, like a `Field`, even though the tool hands it one. Everything
downstream of the footprint is already per cell: `PlacementRules.Check`
iterates cells, `TouchesRoad` is a footprint-level test that already excludes
the placement's own cells, the ghost draws a cell per plan entry, the registry
indexes cell → structure, and demolition walks the footprint. The one line
that would change is the tool's `Footprint`, from the hovered cell to
`WorldGrid.RectCells(cell, cell + size - 1)`.

**Where a building parts company with a field** — the one deliberate
asymmetry: a building is **atomic**. A field shrinks cell by cell as it is
bulldozed and only disappears with its last cell; clearing *any* cell of a
building demolishes the whole thing, taking the rest of its footprint with it,
because half a mill is not a mill. `WorldGrid.SetTile` does that
(`DemolishStructureAt` detaches the whole footprint from the registry *before*
writing any tile, so the clearing writes can't re-enter it). Today, with 1×1
footprints, the two rules are indistinguishable — the difference is written now
because it is the multi-tile question, not later when four cells make it
urgent.

Road access is checked at *placement* only; nothing re-checks it when the
player bulldozes the road afterwards. That is deliberate — cut-off buildings
are M5's problem, when there is delivery to fail.

`PlaceStructure` is also the unvalidated entry point for dev and scenario code,
the way `MarkField` and `BuildRoadLine` are: it clears whatever held the cells
rather than refusing.

### Bulldozing (`src/ui/build/BulldozeTool.cs`, `src/world/Removal.cs`)

> **Anything the player placed can be taken back off, and the terrain under it
> is never touched.** Drag a rectangle with the bulldozer (palette key **4**) and
> every road, field cell and building inside it comes off; the ground that was
> hidden under it comes back exactly as it was, fertility and all, because the
> two layers were never stored together.

`BulldozeTool` is the fourth `BuildTool` and the first that removes rather than
places, which is why it is the only one that had to bend the base instead of
just filling in the contract.

**Legality is inverted, so it gets its own rules.** None of the building rules
describe a removal. `BuildableTerrain` asks whether soil could be built on —
irrelevant to whether something can be taken off it — and `VacantCell` demands
the exact opposite of what this tool is for. Bending either into shape would
have made both rules mean two things. So the bulldozer opts into the two rules
that *do* describe removal, `InBounds` and `OccupiedCell`, and there is no
third one: **what a cell holds never makes it un-removable.** The two things
it can be refused for are the map edge (`OffMap`, the same bound every tool
works inside) and an empty cell (`NothingToClear`). Bare rock is refused for
holding nothing, *not* for being rock — that difference is asserted in
`BuildSmokeTest`, because it is the whole point of not reusing
`BuildableTerrain`.

**A mixed drag clears what is there and skips what is not.** Building is
all-or-nothing; for removal that would be an unusable tool — clearing a
farmyard means dragging over the gaps between its buildings, and one empty
cell would refuse the lot. So the bulldozer is the one user of
`FootprintPolicy.AnyCell`: the drag is refused only when there is nothing in
it at all. The ghost is what keeps that honest — it draws the cells that will
actually be cleared in the legal colour and dims the rest, so what the player
sees before the click is exactly what the click does. The same rule applies to
the *anchoring* click, which is the one cell under the cursor: a drag still has
to start on something removable. That is a deliberate consequence rather than a
special case, and it is the piece to revisit first if bulldozing ever feels
fiddly in play.

**Field shrinks, building goes whole — as the player experiences it.** Both
registries' removal rules (see **Fields** and **Structures**) reach the player
through this one tool, and they differ: take a bite out of a field and what is
left is still that field, under the same name, until the last cell goes and it
is dropped; clip *any* single cell of a building and the whole building goes,
because half a mill is not a mill. A drag that catches one corner of a 2×2
therefore removes all four cells. The tool implements neither rule — every cell
goes through `WorldGrid.Clear`, which goes through `SetTile`, where both rules
already lived.

**The refund seam is an M7 stub.** Every removal passes through exactly one
function, `BulldozeTool.RefundFor(Removal)`, which is where a price will be
put on it (`Apply` credits what it returns, on the line after the call — the
line the money counter takes over). It pays **nothing** today, deliberately rather than unfinished: nothing
has a build cost yet, so no fraction of one exists to give back, and the refund
*economics* — what fraction, whether it varies by building, whether a bulldozed
field returns anything — are M7's to design and are explicitly not designed
here. What the seam does carry is everything a real rule needs: `Removal` names
the tile kind, the entity (`Field` or `Structure`, kept as the object so a
building can be priced by *what* it is once the roster exists), the cells that
were actually freed, and the cell the player hit. The money counter that comes
next credits the amount; neither the signature nor its callers have to move for
it. `BulldozeTool.Removals` is the ledger of what went through the seam, in
order — one entry per *removal*, not per cleared cell, which is why clipping
two cells of the same building refunds it once.

**Roads under vehicles are a real case, from M5.** A road cell can be cleared
out from under a machine that is driving over it, or that has it in a route it
already computed. Nothing prevents or re-validates that today — machines walk
random road paths and would simply fail to path next time — and it is left
open rather than assumed away: the note lives on `WorldGrid.Clear`, where
whoever writes M5's vehicles will be standing.

**The refund seam is wired to the money now** — `Apply` credits whatever
`RefundFor` returns to the `Economy` — and still pays zero, because zero is
what `RefundFor` returns. See **Money and build costs**.

### Money and build costs (`src/world/Economy.cs`)

> **Building costs money, and "you cannot afford this" is a placement refusal
> like any other** — decided with the rest of the verdict, so the ghost shows
> it before the click instead of the click discovering it. Money itself is a
> **stub number** until M7 gives it a market: this is the plumbing, not the
> economy.

`Economy` is a `Node` in `Main.tscn` and **the one thing that owns the
balance**. Everything that moves money goes through it, so there is one number,
one place it changes, and one thing to serialize when saves arrive:

| Member | Meaning |
|---|---|
| `[Export] int StartingBalance` | what the player starts with (**5000**, a placeholder) |
| `[Export] Label? Readout` | the HUD label the balance is written to; null is legal (no HUD, e.g. a dev scene) |
| `int Balance` / `string Text` | the money, and what the readout says |
| `bool CanAfford(int)` | whether that much could be spent right now |
| `bool TrySpend(int)` | takes it out **or refuses and changes nothing** — the balance can never go negative however a caller is written; a spend of zero is a free no-op |
| `void Credit(int)` | puts money in (refunds today, deliveries and sales later); non-positive is ignored, so the M7 stub's refund of 0 is a genuine no-op |
| `void SetBalance(int)` | the write the other two share, and the seam a save-load — or a test that wants the player broke — uses |
| `static string Describe(int)` | `"money: 5,000"`, `InvariantCulture` like the fertility readout, so a test can predict it |

**Cost is a validation input, not a post-hoc check.** A tool's price
(`BuildTool.CostPerCell`) and the current balance go into
`PlacementRules.Check` as a `PlacementBudget` — a *value*, never a handle on
the account, which is what keeps the evaluator the pure, node-state-free thing
it was. `Check` prices the placement (`PlacementPlan.Cost`) and, when the total
is out of reach, refuses it with `PlacementRefusal.CannotAfford` ("not enough
money"). Everything downstream then works unchanged: the hover square goes red,
the ghost dims, `ClickCell` rejects the click and logs the reason. Testing
affordability inside `ClickCell` instead would have made the ghost lie — it
would have shown a placement as legal that the click then refused — which is
the whole reason the price lives in the plan.

Like `TouchesRoad`, affordability is a property of the **whole placement**: a
ten-cell road at five each costs fifty, and the player buys all of it or none
of it, never "the cells that were individually affordable". No single cell is
the offender either, so the ghost dims the placement and blames nobody. It is
judged **last**, after every reason that is not about money, so a drag into
water is refused for the water.

**What a placement is priced for is the cells it acts on.** Under `EveryCell`
that is the whole footprint — a build is all-or-nothing, so there is nothing
else it could be. Under `AnyCell` it is only the cells that pass: a bulldoze
drag crosses empty ground as a matter of course, and charging for cells nothing
happens to would be charging for nothing.

**Charged on commit, never on preview.** `ClickCell` spends `plan.Cost` on the
second click (the first only anchors) and nothing else in the tool touches
money. Previewing a placement — however long the drag is held, however often
the ghost is recomputed — moves nothing. A refused click is free.

**The knobs are exported and wired in `Main.tscn`**: `StartingBalance` on the
`Economy` node, `CostPerCell` on each of the four tools, so tuning is an editor
change and not a rebuild. Each tool's constructor carries the same placeholder
so a code-built tool is priced too; the scene value wins. A tool with **no**
`Economy` wired builds for **free** — that is "there is no money in this scene",
not "the player is broke", and it is what keeps a dev scene or a rules-only
test working exactly as before.

**On screen**, the balance is a `Label` (`Hud/MoneyReadout`) in the **top-right
corner**, anchored to the right edge and right-aligned, opposite the cell
readout in the top-left; `Economy` writes it on every balance change. What each
tool *charges* is on its palette button, read straight off `CostPerCell` (see
**Build palette**), so the price the bar quotes is the price the plan is
validated against.

**Refunds still pay nothing, but the wiring is real.** `BulldozeTool.Apply`
credits whatever `RefundFor(Removal)` returns to the `Economy`, so the day M7
puts a fraction of a build cost in that one expression is the day refunds start
appearing in the balance, with nothing else touched. `RefundTotal` is the
running total of what it has paid — the number the balance can be held against
(after a bulldoze the balance has moved by exactly that, which is how
`BuildSmokeTest` asserts the seam rather than the amount).

**Out of scope, deliberately**: earning money (M5's depot), prices moving (M7),
wages (M5). Nothing yet puts money *in* except a refund of zero.

## Cell picking (`src/ui/CellPicker.cs`)

One implementation of "which cell is under that pixel", shared by every
mouse-driven tool — all four build tools and the hover readout — so the answer
can never drift between them. A static class, not a node: it holds no state.

The camera ray for a screen position is intersected with the ground plane at
y = 0 **analytically** (`Plane.IntersectsRay`), then handed to
`WorldGrid.WorldToCell`. No physics bodies, no collision layers, no raycast
query: picking stays exact, deterministic, and independent of what happens to
be drawn on the cell (a tall rock mesh must not change which cell a pixel
means). Picks are **not clamped** to the map — off-map picks come back as real
coordinates and callers ask `InBounds`/`GetTerrain` what that means.

`CellAt(world, camera, screenPosition)` is the core; `CellAt(node, world,
screenPosition)` takes the camera from the node's viewport (what scene nodes
want), and `CellUnderMouse(node, world)` adds the cursor position. Every
caller's per-frame path ends in the same two lines, and the position-driven
overloads are what lets the headless test drive a known pixel.

## Hover readout (`src/ui/CellInspector.cs`)

The debug instrument that confirms the generated world is what the generator
thinks it is: a screen-corner `Label` naming the cell under the cursor.

```
cell: 12, -3
terrain: soil   fertility: 0.62
tile: road
```

- Three lines, all four facts: coordinates, terrain, fertility, placed tile.
  Fertility prints only for soil (rock and water have none by definition, so
  they read `fertility: -`), formatted with `InvariantCulture` so the text is
  the same on every machine. A cell a field or a building owns names it too
  (`tile: field (Field 1)`, `tile: structure (Structure 1)`) — because the
  entity, not the cell, is what the game addresses farmland and buildings by.
- **Off the map** needs no extra bounds check: the terrain layer already
  answers `TerrainType.OutOfBounds` there, which prints as
  `terrain: off the map` — the cell coordinates are still real and still shown.
- `CellInspector` is a plain `Node` in Main.tscn with `[Export]`s for the
  `WorldGrid` and the `Label` (under a `Hud` `CanvasLayer`); it owns the
  label's visibility. `Inspect(screenPosition)` does the pick + refresh and
  returns the cell — `_Process` calls it with the mouse position, the smoke
  test with a computed pixel.
- **Toggle:** dev key 8. It starts **on** (`EnabledOnStart`), because a dev
  instrument that needs arming isn't one; `ScreenshotTest` calls
  `SetEnabled(false)` before capturing so the canonical views stay clean.

This is *not* the player-facing inspector — field inspection is M4, building
inspection M6. It is a dev readout that happens to be on screen.

## Dev smoke tests (`scenes/dev/`, `src/dev/`)

Headless end-to-end checks; each instances `Main.tscn`, drives it for a few
(simulated) seconds, prints `PASS`/`FAIL` lines, and exits 0/1:

```
godot --headless --path . res://scenes/dev/CameraSmokeTest.tscn
godot --headless --path . res://scenes/dev/WorldSmokeTest.tscn
godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn
```

- **CameraSmokeTest** feeds synthetic input (`Input.ActionPress` for held
  actions; `Input.ParseInputEvent(InputEventAction)` for event-driven ones,
  which is required to reach `_UnhandledInput`) and asserts the rig pans,
  rotates, and zooms.
- **WorldSmokeTest** asserts the generated **terrain** (every in-bounds cell
  has terrain and a GridMap item; cells past the edge report `OutOfBounds`;
  rock, water and soil all exist; fertility stays in 0..1, varies, and is 0 on
  rock/water; the start-road strip is soil), then **seed determinism**:
  regenerating the same seed reproduces terrain and fertility bit-exactly,
  a different seed produces a different map, and returning to the seed
  restores the original. It then asserts the **layers are independent** —
  placing a road leaves `GetTerrain`/`GetFertility` unchanged and only swaps
  the GridMap item, and clearing it restores that item. After that: exactly
  the starting road placed (33 road cells, no machines), dev key 9 spawns a
  machine that has moved after ~3 s and is still on the road, and the
  road-build tool works (the palette arms it and puts it down again, two
  `ClickCell` calls
  place a diagonal road — over cells this seed generates as clear soil, since
  the tool validates placement now; refusal itself is `BuildSmokeTest`'s
  subject — `FindRoadPath` across it returns the corner-cutting
  diagonal walk, and `SmoothRoadPath` collapses that to a single straight
  segment). Finally the **hover readout**: headless has no cursor, so instead
  of moving a mouse the test projects a known cell center to its pixel
  (`Camera3D.UnprojectPosition`) and drives that pixel back through the picker
  — a round trip that fails if either half of screen ↔ cell drifts. It asserts
  the picker, the inspector and the road tool all resolve the *same* cell from
  the *same* pixel (that is the "one shared code path" check), that the label
  mirrors the inspector's text, that the readout names all four facts for the
  origin, that an off-map pixel still resolves to its real coordinates and
  reads "off the map", and that dev key 8 switches the readout off and on.
- **BuildSmokeTest** is where build mode (M2) is asserted, and where the rest
  of M2 adds its checks: the `BuildTool` base, `PlacementRules`, the ghost,
  each tool in turn, the palette they are chosen from, and what a placement
  costs (531 assertions today).
  The **palette** is asserted first, because everything after it is armed
  through it: the four entries in bar order, each naming its tool and printing
  the key that arms it; the bar's rectangle on screen, along the bottom and
  clear of both corner readouts; that selecting an entry arms exactly that tool
  and disarms the rest, and that the bar follows a tool armed or disarmed by any
  other route — including the Esc and right-click cancels further down, which
  are asserted across frames with nobody telling the palette anything; that each
  button's price is the tool's `CostPerCell`, checked by retuning the export at
  runtime and watching the label move; and the M8 availability seam — a locked
  entry is greyed, unclickable and refused by `Select` and `Toggle` too, a
  hidden one is off the bar, locking the armed tool disarms it, and everything
  is handed back before the next section runs.
  The **road tool** goes next, driven through the public cell API — the palette
  arms it (key 1, the accelerator, so that path is proven too),
  `HoverAt`/`ClickCell`/`Cancel` do the rest — covering **place**
  (anchor, all-legal ghost over the whole line, second click writes exactly
  that line, road-over-road stays legal), **refuse** (rock under the cursor
  tints the hover square red and cannot even be anchored; a drag into water
  ghosts one offending cell and no legal-coloured cell, the click is rejected,
  *nothing* of the line is built and the anchor survives; an occupied cell and
  an off-map cell each refuse with their own reason; the road-access rule is
  checked straight through `PlacementRules`), and **cancel** (Esc and right
  click each drop the anchor first and leave the tool second, clearing ghost
  and preview).
  The **field tool** follows, so it validates against a world that already
  holds those roads. The palette arms it and — the tool-group rule — disarms the
  road tool. It asserts `RectCells` on its own (inclusive of both corners, no
  duplicates, and the same list dragged from any of the four corners), then a
  rectangle marked from its far corner: the whole rectangle previews legal and
  writes nothing until the second click, then every cell is a field tile,
  `Fields` grows by exactly one, and `GetField` answers with the *same* `Field`
  for every cell, whose `CellCount` and `Bounds` are the drag. A second
  rectangle sharing an edge stays a **second** field. Refusals: a field cell
  cannot even be anchored on, a rectangle over a field or over a road is
  refused as `Occupied` (with the `NoOverlap`-would-have-allowed-it contrast
  asserted directly through `PlacementRules`), and one straddling rock or water
  is refused whole — not even its soil cells are marked. Finally, `SetTile`
  over a field cell shrinks its field, and the field disappears when its last
  cell goes.
  The **structure tool** comes last, because the rule it exists for only means
  something once there is a road network to touch. The palette arms it (and
  disarms the field tool — the group rule again, now with three members). The
  accepted case: free soil beside a road previews legal, ghosts one legal cell
  *without* an anchor, and goes down on a **single** click — after which the
  cell holds a `Structure` reachable both by cell and by `Id`, with the
  registry, the cell count, `Origin`, `Bounds` and the hover readout's name all
  asserted, and that same cell now previewing as occupied. The road rule is
  then taken through all three of its verdicts on **one** cell, with the roads
  laid rather than searched for so nothing else changes between the answers:
  refused as `NoRoadAccess` with nothing written (and the ghost dimming the
  whole placement, blaming no cell); still refused when the only road nearby
  meets it diagonally — that is the 4-neighbour check; accepted the moment a
  road shares an edge, placing a second building with a higher id. Refusals
  after that: rock or water, checked with a road laid beside it so the terrain
  is unambiguously the reason; and a building, a road and a field cell each
  refused as `Occupied` (all three have road access, so only vacancy is
  talking). Finally, clearing the cell demolishes the building — tile, cell
  lookup, registry entry and id all gone, terrain intact, the other building
  untouched.
  The **bulldozer** comes last of all, because it needs one of everything on
  the map before it can take anything off again; the palette arms it and
  disarms the structure tool (the group rule, now with four members). It
  places a road, a field and a building **through their own tools** and then
  bulldozes each away, and the headline runs underneath all three: the terrain
  type and fertility of every cell are read *before* anything is built on them
  and compared again once everything has been cleared — exact float
  comparison, and the check fails on a cell it never remembered, so it cannot
  pass by comparing nothing. Then the two registry rules as the player meets
  them: a field bitten in the middle shrinks and keeps its identity, and is
  dropped only when the finishing drag takes its last cell, while a 2×2
  building (placed through `PlaceStructure`, since the tool only offers 1×1)
  clipped along one edge loses its whole footprint and its id. The mixed drag
  is asserted in both directions — a rectangle half road and half bare ground
  previews **legal**, ghosts exactly the cells it will take as legal and dims
  the rest, blames no cell, and clears only the built half — and so are the two
  refusals a bulldozer has: empty ground *and bare rock* both answer
  `NothingToClear` (the terrain is not the reason), off the map answers
  `OffMap`, and no refused click reaches the refund seam. The seam itself is
  asserted as a seam: one entry per removal (a clipped building appears once,
  and a cell already cleared inside the same drag does not appear at all), each
  naming its kind, its entity, the cells actually freed and where the player
  hit it, across all three kinds — and a total refund of zero, the M7 stub
  doing exactly what it says.
  **Money comes last of all**, because paying for a placement needs every tool
  already proven. The starting balance is asserted before anything is built —
  the exported number, the readout showing it, every tool wired to the one
  account, and the readout's own rectangle on screen and clear of the cell
  readout — and the balance is then set to a working million, so no assertion
  about *legality* can fail for want of funds; every money check sets the
  balance it is about. The checks: a drag priced as a **total** by the plan, a
  preview that moves nothing however long it is held, and a commit that deducts
  exactly `plan.Cost`; a building one coin short of its price refused as
  `CannotAfford` with the ghost dimmed, no cell blamed, nothing built and the
  balance untouched — then bought by the very same click once the money is
  there, so it is the money and not the ground that changed; four cells of road
  affordable at exactly their total while five are not, though every one of the
  five is affordable on its own; the `AnyCell` rule asked straight through
  `PlacementRules` with a budget (a half-built rectangle is priced for the cells
  it clears, not for the ground it crosses, while a build is priced for its
  whole footprint) and then through the bulldozer itself, with a price put on it
  at runtime through the export — which is both the tunable knob and the only
  way to watch an `AnyCell` footprint actually pay. Then the account's own
  invariant (`TrySpend` refuses rather than going negative, a negative spend is
  not a credit through the wrong door, a credit of zero is a no-op) and the
  refund seam: after a bulldoze the balance has moved by exactly the change in
  `RefundTotal` — asserted in those terms, so it stays true when M7 makes the
  seam pay — which is zero today.
  It picks its cells by **searching the generated terrain at runtime**
  (nearest rock, a clear soil run, a soil run ending in water, a clear soil
  block for the rectangles, a strip running into rough ground, free soil
  beside the road network) instead of hard-coding coordinates a seed change
  would invalidate — reuse those helpers rather than writing literal cells into
  new assertions. The field, structure and bulldoze sections search at the
  point of use, because by then the test's own roads, fields and buildings are
  on the map and "clear soil" has to mean clear *now*.

### Canonical views (`scenes/dev/ScreenshotTest.tscn`)

The visual counterpart to the smoke tests: it renders the canonical views to
PNG so a human — or an agent — can look at what the game actually draws.
**Must run windowed** (`--headless` is the dummy rasterizer: no framebuffer to
read back, and the run hangs), output dir as a user arg:

```
godot --path . res://scenes/dev/ScreenshotTest.tscn -- <dir>
```

`01-start` (default pose), `02-ghost-legal` / `03-ghost-refused` (the build
ghost in both verdicts), `04-field-ghost` / `05-field-marked` (a field
rectangle previewed mid-drag, then the same rectangle committed),
`06-structure-refused` / `07-structure-placed` (the must-touch-a-road rule
refused before the click, then a building standing beside the road),
`08-bulldoze-mixed` (the one ghost painted per cell: a drag that is legal
overall while dimming the cells it will skip), `09-rotated`, `10-zoomed-out`,
`11-overview` (detached diagnostic camera), `12-readout` (the hover readout). Each `PASS` line captions what the frame is
meant to show — the ghost views list their cells and per-cell refusals, so the
caption, not the pixel colour, is what says which cell killed a drag.

Where a view needs a legal spot on the map, it *searches* for one through the
tool itself rather than hard-coding cells, for the same reason
`BuildSmokeTest` does: a seed change must not quietly turn a view into a
picture of something else. `04-field-ghost` leaves its field on the map on
purpose, and `07-structure-placed` its building, so the rotated, zoomed and
overview shots carry both too. `08-bulldoze-mixed` only ever hovers its drag —
committing it would take the start road out of every view that follows.

Every view carries the **build palette** along the bottom, and the views that
arm a tool (02–08) show that tool's button lit: the screenshot run arms tools
with `SetActive`, never through the bar, so the lit button is the palette's
reflection rule in a picture.

Views settle by **time**, not frame count (the rig smooths on `delta`, so a
frame count converges differently on a fast machine). Anything driven by the
cursor is pinned instead: a screenshot run has no mouse, so the readout and
the build tool get `SetProcess(false)` and are fed an explicit cell. The
ghost views find their drag by asking the tool over a window around the origin
until a legal and a refused verdict each turn up, so a seed change cannot
quietly make them two pictures of the same thing.

For a visual check without a window grab, Godot's movie-maker mode renders
frames to PNG: `godot --path . --write-movie out/frame.png --fixed-fps 30
--quit-after 90 --resolution 1280x720`.

## Not yet implemented (deliberate)

- Fields have no behavior — a `Field` is a named region and nothing more.
  "Workable" starts when machines get jobs, fertility is generated but nothing
  reads it yet, and there is no crop, yield or rename UI (all M4).
- Structures have no behavior either, and there is only the one generic kind:
  a `Structure` is an identified building that occupies ground and must touch a
  road. The roster and what buildings *do* is M5 (silo) and M6 (cleaner, mill,
  bakery); multi-tile footprints are unwritten but not designed out (see
  **Structures**) — `PlaceStructure` already takes any footprint, which is how
  `BuildSmokeTest` gets a 2×2 to clip; nothing re-checks road access when the
  road beside a building is bulldozed away later.
- Validation lives in the **tools**, not in the data layer: `WorldGrid.SetTile`
  still writes anywhere (including off the map), which is what start layout,
  dev code and tests want. Anything the *player* places goes through
  `BuildTool`, and therefore through `PlacementRules`.
- Player interaction is the build palette plus the road, field, structure and
  bulldoze tools, and two dev keys beside them. The palette has the *seam* for
  unlock gating (`ToolAvailability`) and none of the rules — what unlocks a tool
  is M8's — and no entry is ever anything but `Available` today. The full HUD
  pass is M10's; the bar and the two corner readouts are all the UI there is.
- Money is a **stub number**: placements are charged and the balance is shown,
  but nothing puts money *in* — earning is M5's depot, prices moving are M7's,
  wages are M5's — and every price is a placeholder chosen to be tunable rather
  than balanced (see **Money and build costs**).
- Removing something refunds **nothing**, on purpose: `BulldozeTool.RefundFor`
  has the shape of a refund and none of the economics, which are M7's. What it
  returns *is* credited to the `Economy` now, so only the number is missing.
- Nothing stops a road being bulldozed out from under a machine that is driving
  it or has it in a route. That is a real case from M5, not an impossible one;
  the note sits on `WorldGrid.Clear`.
- The simulation still lives in Godot nodes; the standalone deterministic sim
  core (ECS-like layout, save/replay) comes when there's real sim state to own.
- Flow fields: BFS per machine is fine at this scale; revisit when mover count
  grows.
