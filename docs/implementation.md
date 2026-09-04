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
src/ui/            MenuController.cs (keyboard menu), RoadBuildTool.cs
src/world/         TileType.cs, WorldGrid.cs, Machine.cs
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
land), and **machines** (vehicles that move around the map). Roads and fields
are static grid tiles; machines are moving scene entities — deliberately *not*
grid cells.

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
  edge of the band) and `BuildRoadLine` (sets those cells to road).
  Machines still *drive* such a road diagonally: the corner-cutting rule lets
  both the BFS and the smoothing pass run straight along the staircase.
- **Start layout** (`GenerateStartRoad`, deterministic): a single straight
  road along x through the origin (cells −16..16 at z = 0), on the soil strip
  generation carved for it. Nothing else is placed — fields and further roads
  will come from gameplay/build actions. Building outside `MapHalfExtent` is
  still allowed (placement validation is M2); such cells are drawn too.
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
`Menu` node in Main.tscn. **1** toggles the road-build tool, **9** calls
`WorldGrid.SpawnMachine()`; the other slots log "unassigned". The `World` and
`RoadTool` references are node `[Export]`s wired in the scene.

## Road-build tool (`src/ui/RoadBuildTool.cs`)

The first mouse-driven build action. A `RoadTool` node in Main.tscn; menu
key 1 toggles it. While active:

- A **blinking square** (unshaded translucent `PlaneMesh`, visibility cycled
  at 0.5 s) highlights the hovered cell. Picking casts the camera ray from
  `Viewport.GetMousePosition()` against the ground plane (y = 0) — no physics
  involved — then `WorldGrid.WorldToCell`.
- **First left click** anchors the road start; a preview line (a `MultiMesh`
  of the same squares over `WorldGrid.LineCells`) follows the cursor.
- **Second left click** places the road via `WorldGrid.BuildRoadLine` and
  re-arms the tool for the next road.
- **Right click / Esc** cancels the pending anchor first, then deactivates.

Highlights sit at `Machine.DeckHeight + 0.05` so they never z-fight the road
deck. `ClickCell` (the anchor/place step) is public so the headless smoke test
can drive the tool without a real cursor.

Gotcha (hand-written .tscn): a Node-typed export serialized as
`World = NodePath("../World")` only resolves to the actual node if the
`[node]` header also carries `node_paths=PackedStringArray("World")` —
without that marker the property loads as null.

## Dev smoke tests (`scenes/dev/`, `src/dev/`)

Headless end-to-end checks; each instances `Main.tscn`, drives it for a few
(simulated) seconds, prints `PASS`/`FAIL` lines, and exits 0/1:

```
godot --headless --path . res://scenes/dev/CameraSmokeTest.tscn
godot --headless --path . res://scenes/dev/WorldSmokeTest.tscn
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
  place a diagonal road, `FindRoadPath` across it returns the corner-cutting
  diagonal walk, and `SmoothRoadPath` collapses that to a single straight
  segment).

For a visual check without a window grab, Godot's movie-maker mode renders
frames to PNG: `godot --path . --write-movie out/frame.png --fixed-fps 30
--quit-after 90 --resolution 1280x720`.

## Not yet implemented (deliberate)

- Fields have no behavior — "workable" starts when machines get jobs, and
  fertility is generated but nothing reads it yet (crop growth is M4).
- Terrain does not restrict building: roads and fields can be placed on rock,
  water, or right off the map. Placement validation is M2 — `GetTerrain`,
  `IsSoil` and `InBounds` are the hooks it will ask.
- Player interaction is the keyboard menu plus the road-build tool; there is
  no other tile painting/building UI yet, and no build costs or validation
  (roads can be drawn anywhere, over fields included).
- The simulation still lives in Godot nodes; the standalone deterministic sim
  core (ECS-like layout, save/replay) comes when there's real sim state to own.
- Flow fields: BFS per machine is fine at this scale; revisit when mover count
  grows.
