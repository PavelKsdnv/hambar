## World grid (`src/world/`)

Four entity kinds: **roads** are tiles; **fields** and **structures** are a
tile *plus* an entity owning those cells, the tile saying only *that* something
is placed; **machines** are sim rows and deliberately not grid cells. A field
is both — a registry entry here and a crop row in the sim
([`## Crops`](crops.md)).

**Data/view split.** The child `GridMap` is presentation only and must **never
be read back** — every mutator keeps it in sync
([`## Simulation`](simulation.md)).

**Two layers, stored separately.** What the land *is* (terrain plus fertility,
from the seed) and what the player *built* (`TileType`, sparse) never share
storage — which is what makes bulldozing lossless: clearing back to `Empty`
leaves the terrain untouched. One `GridMap` draws both, placement *hiding*
terrain rather than overwriting it. `GetTerrain` answers `OutOfBounds` rather
than throwing, so "not on the map" is one call.

**Terrain generation** (`GenerateTerrain`, run before the start road):

- The map is **bounded**: `MapHalfExtent` 48 → 97×97 cells = 194 m at 2 m
  cells, fitting Main.tscn's ground plane and slightly larger than full
  zoom-out shows.
- **One seed** drives everything: a fertility noise and a rock/water mask, each
  taking its int from [`## Randomness`](calendar-random.md). The mask reads as
  a coarse elevation — low is water, high is rock, the rest soil — so water and
  rock never border each other. Fertility is **0 on rock and water**: it means
  "how good is this soil".
- Storage is **flat arrays** indexed by cell: the extent they were built with
  is cached, so moving `MapHalfExtent` at runtime can never index past them.
- Re-running it is **bit-exact** for a seed and never touches the placement
  layer. The starting road strip (z = 0, x = -16..16) is **carved** to soil
  rather than biasing the noise, so the seed still owns every other cell.

**A field cell draws its crop stage** ([`## Crops`](crops.md)): one MeshLibrary
item per `CropStage`, consecutive from a base id, the arithmetic the fertility
tiers already use. `TileType` stays at `Field`, because six stages in the tile
enum would put sim state in the placement layer; the plain farmland item is the
fallback for a cell whose row is unreachable.

**How a stage the sim moved reaches the `GridMap`.** Every other tile changes
because something called `SetTile`; a crop ripens because time passed and
nothing calls anything. So `WorldGrid` takes the `ISimView` half as well and
sweeps its fields once a frame against the stage it last drew, pushing only
those that moved — view bookkeeping, never hashed, never saved. Rejected: an
event out of `CropSystem`, which would run the view half-way through a tick and
point the dependency from the sim at the renderer. `MarkField` draws its own
new field rather than waiting for a frame, because a stepped run has none.

**Stage art needs height *and* colour**: at `ZoomMax` only colour reads, close
in height is what separates two browns. **Keep the ramp out of the terrain
palette** — grey is rock, olive is soil — a trap paid for once, when a
grey-brown fallow field read as a rock outcrop at the zoom limit. Warm earth →
green → gold is what is left, and M10 inherits it.

**Road-network queries live here**, because they are questions about the grid
rather than about a vehicle:

- `FindRoadPath` — BFS over road cells, 8-neighbour, where a diagonal step is
  allowed only past a road *corner*; that rule is what stops a path squeezing
  through the point gap between two unconnected strips. `SmoothRoadPath` then
  string-pulls it, dropping every waypoint the vehicle can drive straight past.
- `LineCells` — Bresenham, except a diagonal step stair-steps through two
  orthogonal cells so the footprint stays 4-connected, and the connector cell
  **alternates sides** on successive diagonals so the staircase stays centred
  on the true line; without that, machines drive hugging one edge of the band.
- `RectCells` — row-major from the minimum, so the list depends on the
  rectangle and not on the corner the drag started from.

**Mutators.** `SetTile` is the single write path; `MarkField`, `PlaceStructure`
and `BuildRoadLine` are the **unvalidated** doors, for start layout, dev keys
and tests, while anything the *player* places goes through a `BuildTool` and so
`PlacementRules`. `Clear` returns **null** — not an empty `Removal` — for a
cell that held nothing, which is load-bearing below.

Two asymmetries in `SetTile` that everything downstream inherits: a field cell
written over detaches **just that cell** (and a field with no cells is
dropped), while **any** cell of a structure demolishes the building. The null
is what stops one building being refunded once per cell a drag clipped.

## Cell picking (`src/ui/CellPicker.cs`)

One implementation of "which cell is under that pixel", shared by every tool
and the hover readout, so the answer cannot drift between them. The camera ray
meets y = 0 **analytically** — no physics bodies — so a pick is exact,
deterministic and independent of what is drawn there (a tall rock mesh must not
move a cell). Picks are **not clamped**: off-map ones are real, and callers ask
`InBounds`.
