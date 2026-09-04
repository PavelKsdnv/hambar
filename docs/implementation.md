# Implementation notes

What exists in the codebase today and how it fits together. Companion to
`tech.md` (the decisions) — this documents how those decisions were realized.
Last updated: 2026-09-04.

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
src/ui/            MenuController.cs (keyboard menu),
                   CellPicker.cs (screen -> cell), CellInspector.cs (hover readout)
src/ui/build/      BuildTool.cs (base for every placement tool), RoadBuildTool.cs,
                   FieldBuildTool.cs
src/world/         TileType.cs, WorldGrid.cs, PlacementRules.cs, Field.cs, Machine.cs
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

Three entity kinds exist: **roads** (machine-traversable), **fields** (workable
land), and **machines** (vehicles that move around the map). Roads are plain
grid tiles; a field is a tile layer *plus* a `Field` entity that owns those
cells (see **Fields** at the end of the build-tools section — that is the unit
farmland is addressed by); machines are moving scene entities — deliberately
*not* grid cells.

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
- **placement** — `TileType` (`Empty | Road | Field`). The player owns it;
  clearing a cell back to `Empty` leaves the terrain underneath untouched.

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
- **Start layout** (`GenerateStartRoad`, deterministic): a single straight
  road along x through the origin (cells −16..16 at z = 0), on the soil strip
  generation carved for it. Nothing else is placed — fields and further roads
  will come from gameplay/build actions. `SetTile` itself still writes outside
  `MapHalfExtent` (legality is a tool-level concern, see the build tools
  below); such cells are drawn too.
- **Dev tiles** (`assets/dev/tile_library.tres`, a hand-written `MeshLibrary`):
  road = flat gray box (item 0), field = raised brown box (item 1), rock =
  tall gray block (item 2), water = thin dark-blue slab sitting lower than
  soil (item 3), and soil in **four fertility shades** (items 4–7, pale straw
  → deep green) so the fertility field is legible in the iso view. Item ids
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
  default is 0 — the game starts with no machines; spawn them via menu key 9.

## Keyboard menu (`src/ui/MenuController.cs`)

A minimal command menu on the number keys: actions `menu_1`..`menu_9` in
`project.godot` map keys 1–9 to slots, handled in `_UnhandledInput` on the
`Menu` node in Main.tscn. **1** toggles the road-build tool, **2** the hover
readout, **3** the field-marking tool, **9** calls `WorldGrid.SpawnMachine()`;
the other slots log "unassigned". The `World`, `Inspector` and tool references
are node `[Export]`s wired in the scene — the tool slots are typed as the
`BuildTool` base, so a slot (or the M2 palette) can point at any build tool
without touching the menu. The menu never has to *disarm* anything either:
arming a tool disarms the rest through the tool group (below).

## Build tools (`src/ui/build/`, `src/world/PlacementRules.cs`)

Every mouse-driven placement tool sits on one base, `BuildTool` (M2's
foundation): hover highlight, ghost preview, validation that refuses an
illegal placement *before* the click, and the anchor → preview → place →
cancel interaction. `RoadBuildTool` was the first subclass and is 30 lines,
most of them comment — copying it is how the next tool gets written, and
`FieldBuildTool` is that copy plus one override.

**What a subclass supplies** (the whole contract):

| Member | Meaning |
|---|---|
| `TileType PlacedTile` | what the tool writes; `NoOverlap` treats a cell already holding it as a no-op |
| `PlacementRule Rules` | the legality rules this tool opts into |
| `IReadOnlyList<Vector2I> Footprint(anchor, cell)` | cells a drag covers (roads: `WorldGrid.LineCells`, a line; fields: `WorldGrid.RectCells`, a filled rectangle) |
| `void Apply(PlacementPlan)` *(virtual)* | writes the placement; the default stamps `PlacedTile` over every cell |
| `bool NeedsAnchor` *(virtual, true)* | false for tools that place on a single click, which then ghost as soon as the cursor moves |

**The tools that fill it in** (one row per subclass; the remaining M2 tools —
structures, bulldoze — add their rows here):

| Tool | Menu key | Places | Rules | Footprint | `Apply` |
|---|---|---|---|---|---|
| `RoadBuildTool` | **1** | `Road` | `BuildableTerrain` + `NoOverlap` | `LineCells` — a stair-stepped line | default (stamps tiles) |
| `FieldBuildTool` | **3** | `Field` | `BuildableTerrain` + `VacantCell` | `RectCells` — the filled rectangle between the corners | overridden: `WorldGrid.MarkField`, which creates the `Field` entity as well as the tiles |

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
- `TouchesRoad` — a *footprint-level* rule: some cell of the placement must
  share an edge with a road cell **outside** the footprint, so a placement can
  never satisfy its own road requirement. No tool opts in yet (the M2
  structure tool will); `BuildSmokeTest` exercises it through
  `PlacementRules.Check` directly so it stays proven until then.

`PlacementRules.Check(world, cells, rules, placing)` returns a
`PlacementPlan`: the cells, a parallel array of per-cell `PlacementRefusal`s,
and the one refusal that describes the whole placement. **Partial legality is
all-or-nothing** — one illegal cell refuses the entire drag, and nothing lands
on "the legal part" of it. The per-cell verdicts exist only so the ghost can
point at the cells that caused the refusal. `PlacementRules.Explain(refusal)`
gives the player-facing wording (a build-cost/HUD issue can reuse it).

**What the player sees.** The blinking hover square is yellow
(`BuildTool.CursorLegal`) or red (`CursorRefused`) by the verdict on the
placement under the cursor. Once anchored, a `MultiMesh` of the same squares
ghosts the footprint with **per-instance colours** (`UseColors`, white albedo
with `VertexColorUseAsAlbedo`): `GhostLegal` yellow when the whole placement
is allowed, otherwise `GhostIllegalCell` on the offending cells and
`GhostRefused` on the rest — a refused drag never shows a legal-coloured cell,
because none of it is going to be built. Highlights sit at
`Machine.DeckHeight + 0.05` so they never z-fight the road deck.

**Interaction.** First left click anchors (and is itself validated: you cannot
anchor on rock, or on a cell a field already owns), second left click places
and re-arms. A refused click writes nothing *and keeps the anchor*, so the
player just re-aims. Right click / Esc drops the anchor first and leaves the
tool second.

**One armed tool at a time.** Every tool joins the `build_tools` scene group
(`BuildTool.ToolGroup`) in `_Ready`, and `SetActive(true)` disarms every other
member of it — so two tools can never listen to the same click, and a disarmed
tool drops its anchor, ghost and preview on the way out. It lives in the base
rather than in the menu on purpose: a new tool gets the behaviour for free, and
neither the menu nor the coming palette has to know the full list.

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

`FieldBuildTool` (menu key **3**) is the smallest possible `BuildTool`
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

## Cell picking (`src/ui/CellPicker.cs`)

One implementation of "which cell is under that pixel", shared by every
mouse-driven tool — the road and field tools and the hover readout today, M2's
remaining tools next — so the answer can never drift between them. A static class, not a node:
it holds no state.

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
  the same on every machine. A cell a field owns names it too
  (`tile: field (Field 1)`) — because the field, not the cell, is what the
  game addresses farmland by.
- **Off the map** needs no extra bounds check: the terrain layer already
  answers `TerrainType.OutOfBounds` there, which prints as
  `terrain: off the map` — the cell coordinates are still real and still shown.
- `CellInspector` is a plain `Node` in Main.tscn with `[Export]`s for the
  `WorldGrid` and the `Label` (under a `Hud` `CanvasLayer`); it owns the
  label's visibility. `Inspect(screenPosition)` does the pick + refresh and
  returns the cell — `_Process` calls it with the mouse position, the smoke
  test with a computed pixel.
- **Toggle:** menu key 2. It starts **on** (`EnabledOnStart`), because a dev
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
  the starting road placed (33 road cells, no machines), menu key 9 spawns a
  machine that has moved after ~3 s and is still on the road, and the
  road-build tool works (menu key 1 toggles it on/off, two `ClickCell` calls
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
  reads "off the map", and that menu key 2 switches the readout off and on.
- **BuildSmokeTest** is where build mode (M2) is asserted, and where the rest
  of M2 adds its checks: the `BuildTool` base, `PlacementRules`, the ghost, and
  each tool in turn.
  The **road tool** goes first, driven through the public cell API — menu key 1
  arms it, `HoverAt`/`ClickCell`/`Cancel` do the rest — covering **place**
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
  holds those roads. Menu key 3 arms it and — the tool-group rule — disarms the
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
  It picks its cells by **searching the generated terrain at runtime**
  (nearest rock, a clear soil run, a soil run ending in water, a clear soil
  block for the rectangles, a strip running into rough ground) instead of
  hard-coding coordinates a seed change would invalidate — reuse those helpers
  rather than writing literal cells into new assertions. The field section
  searches at the point of use, because by then the test's own roads and fields
  are on the map and "clear soil" has to mean clear *now*.

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
`06-rotated`, `07-zoomed-out`, `08-overview` (detached diagnostic camera),
`09-readout` (the hover readout). Each `PASS` line captions what the frame is
meant to show — the ghost views list their cells and per-cell refusals, so the
caption, not the pixel colour, is what says which cell killed a drag.

Where a view needs a legal spot on the map, it *searches* for one through the
tool itself rather than hard-coding cells, for the same reason
`BuildSmokeTest` does: a seed change must not quietly turn a view into a
picture of something else. `04-field-ghost` leaves its field on the map on
purpose, so the rotated, zoomed and overview shots carry one too.

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
- Validation lives in the **tools**, not in the data layer: `WorldGrid.SetTile`
  still writes anywhere (including off the map), which is what start layout,
  dev code and tests want. Anything the *player* places goes through
  `BuildTool`, and therefore through `PlacementRules`.
- Player interaction is the keyboard menu plus the road and field tools. The
  remaining M2 tools — structures, bulldoze — and the build palette and build
  costs are not written yet; they are meant to be subclasses of `BuildTool`
  (plus, for bulldoze, a rule of its own) and assertions in `BuildSmokeTest`.
- The simulation still lives in Godot nodes; the standalone deterministic sim
  core (ECS-like layout, save/replay) comes when there's real sim state to own.
- Flow fields: BFS per machine is fine at this scale; revisit when mover count
  grows.
