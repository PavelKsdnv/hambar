## Build palette (`src/ui/BuildPalette.cs`)

> **The toolbar is how a build tool is chosen**, and the pattern every other
> HUD bar copies ([`## Game calendar`](calendar-random.md) is the second): one
> button per entry from an exported list, one selection path, one repaint a frame.

**Built in code from the exported `Tools` list**, each button reading its name,
icon and price off the tool, so the bar cannot quote a price the click does not
charge — adding a kind is adding a node to that list.

**It is not a second source of truth.** Which tool is armed is a fact about the
tools (`BuildTool.Active`, kept unique by the `build_tools` group), repainted
every frame, so a tool disarmed by *any* route is right on the bar that frame.
Key *n* is an accelerator, not a way around it: it calls the same `Toggle` a
button does, which is also how a headless test arms one.

**The M8 seam is `ToolAvailability`.** A non-available entry is refused on
**every** path in — a seam only one path respects is decoration — and a tool
that stops being available while held is disarmed on the spot. The *rules* are
M8's: nothing here knows what an unlock is.

**Two gotchas in the look.** Icons are tinted with a *modulate*, which
multiplies, so **the source artwork has to be white** — a dark glyph stays dark
whatever colour it is given. And the root `Control` is `MOUSE_FILTER_IGNORE`,
inner labels too, or a full-screen `Control` swallows every click.

## Build tools (`src/ui/build/`, `src/world/PlacementRules.cs`)

Every mouse-driven placement tool sits on one base, `BuildTool`, whose point is
that an illegal placement is refused *before* the click rather than by it;
copying `RoadBuildTool` is how the next tool gets written.

**The rules** are a `[Flags]` set (`PlacementRule`) — add a flag rather than
re-code a check in a tool — and the near-duplicates in it are the point.
`BuildableTerrain` has the looser `InBounds` beside it for the bulldozer: what
may be *taken off* a cell says nothing about what could be built there.
`NoOverlap` and `VacantCell` differ by exactly the "already there" case, letting
a road branch off the network while a field's cell belongs to one `Field`.
`TouchesRoad` is **footprint-level** and 4-neighbour: the road must be *outside*
the footprint, or a placement would satisfy its own access — a corner does not.

`Check` answers a `PlacementPlan`, and how its per-cell verdicts add up is the
`FootprintPolicy`, because building and clearing want opposite answers about a
mixed region. Building is `EveryCell` — **all-or-nothing**, a field with a bite
out of it not being what the player asked for, the per-cell verdicts existing
only so the ghost can point at the offenders. The bulldozer alone is `AnyCell`,
*priced* on the cells it acts on rather than the ground it crossed.

**What the player sees** is a ghost whose one promise is that **a
legal-coloured cell is a cell something will happen to** — hence a virtual
`GhostColorFor`: under `AnyCell` a legal plan still holds cells it will skip.

**Interaction.** A refused click writes nothing *and keeps the anchor*, so the
player re-aims; right click / Esc drops the anchor first, the tool second.

**One armed tool at a time.** Every tool joins the `build_tools` group and
`SetActive(true)` disarms the rest — in the base, not the palette, so a new
tool gets it free and the palette knows no rule.

### Structures (`src/world/Structure.cs`, `src/ui/build/StructureBuildTool.cs`, `src/world/StructureKind.cs`)

> **A building is addressed by one `Structure` entity — never by the cell**,
> reached with `WorldGrid.GetStructure`; the cells carry `TileType.Structure`
> only so the view can draw them and `PlacementRules` can call them occupied.

**Why an entity and not just a tile value.** `TileType.Structure` says that *a*
building is here, not *which*, and identity is needed long before behaviour is:
`#37` sends a vehicle to *a silo* by `Id`, M6 hangs a recipe off *that* mill, a
save has to name it. `Id` is creation order, never reused, resolving to null
once the building is gone, so an order pointing at a demolished mill fails
loudly rather than hitting its replacement. Its `Storage` buffer
([`## Items and buffers`](items.md)) is hashed with the registry entry, so a
building is state before it is an entity row.

**`StructureKind` is the roster, and one tool places every row in it.**
`StructureBuildTool` places whichever `Kind` its own export names, so the depot
(`#38`) is a second Main.tscn node with a different `Kind`, name, price and
`StorageCapacity`, never a subclass. Capacity sits on the *tool*, not the kind,
for the reason `CostPerCell` does — a playtest argues about the number — and
`StructureKinds` only backs a `PlaceStructure` that skips a tool: dev fixtures.

**Where a building parts company with a field** — the one deliberate asymmetry:
a building is **atomic**. A field shrinks cell by cell; clearing *any* cell of
a building demolishes the whole thing, because half a mill is not a mill.
`SetTile` detaches the whole footprint from the registry *before* writing any
tile, so the clearing writes cannot re-enter it. With 1×1 footprints the two
rules are indistinguishable; the difference is written now, not when four cells
make it urgent.

**Trap: fill the cell → structure map *before* writing the tile.** `SetTile`
redraws as it writes and `ViewItem` picks the mesh by reading the kind back out
of that map, so registering afterwards drew every building as the fallback box.
It fails **silently** — the entity is a silo, `Storage` works, every haul passes,
only a rendered frame disagrees; found in a screenshot, not by a test.
`PlaceStructure` then refreshes the footprint explicitly, since `SetTile`
returns early on a cell that already read as a structure and would leave the
demolished building's mesh under its replacement's name. Any per-kind *art*
claim needs its own assertion on the drawn item: nothing else looks at the GridMap.

### Bulldozing (`src/ui/build/BulldozeTool.cs`, `src/world/Removal.cs`)

> **Anything the player placed can be taken back off, and the terrain under it
> is never touched** — the ground comes back as it was, fertility and all,
> because the two layers were never stored together.

**Legality is inverted, so it gets its own rules.** No building rule describes
a removal — `BuildableTerrain` asks whether soil could be built on,
`VacantCell` demands the opposite of what this tool is for — and bending either
into shape would have made it mean two things. So the bulldozer opts into
`InBounds` + `OccupiedCell` and nothing else: **what a cell holds never makes
it un-removable.** Bare rock is refused for holding nothing, not for being
rock.

**A mixed drag clears what is there and skips what is not.** All-or-nothing
would be unusable — clearing a farmyard means dragging over the gaps between
its buildings, and one empty cell would refuse the lot. Hence the sole use of
`AnyCell`, the ghost colouring only the cells that will actually be cleared.
The *anchoring* click is the exception — a drag has to start on something
removable, the first thing to revisit if bulldozing ever feels fiddly.

**The refund seam is an M7 stub.** Every removal passes through `RefundFor`,
whose return `Apply` credits to the `Economy`: the day M7 puts a fraction of a
build cost in that expression is the day refunds appear. It pays **nothing**
today and is handed the entity, so a building can be priced by *what* it is.
`Removals` is the ledger, **one entry per removal, not per cleared cell**.

### Money and build costs (`src/world/Economy.cs`)

> **Building costs money, and "you cannot afford this" is a placement refusal
> like any other** — decided with the rest of the verdict, so the ghost shows
> it before the click instead of the click discovering it. Money itself is a
> **stub number** until M7 gives it a market: this is the plumbing, not the
> economy.

`Economy` **owns the balance**, so there is one number to serialize; `TrySpend`
takes the money **or changes nothing**, so it cannot go negative — an invariant
[`## Labour and wages`](labour.md) works around rather than relaxes.

**Cost is a validation input, not a post-hoc check.** A tool's `CostPerCell`
and the balance go into `PlacementRules.Check` as a `PlacementBudget` — a
*value*, never a handle on the account, which keeps the evaluator the pure
thing it was — and `Check` refuses with `CannotAfford`. Deciding it inside
`ClickCell` instead would have made the ghost lie, showing as legal a placement
the click refused. Like `TouchesRoad` it is a property of the **whole
placement** (all of a ten-cell road or none of it), judged **last**, so a drag
into water blames the water.

**Charged on commit, never on preview**, so a refused click is free however
often the ghost is recomputed. A tool with **no** `Economy` wired builds for
free: that is "there is no money in this scene", not "the player is broke".
