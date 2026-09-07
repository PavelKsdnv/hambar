## Orders (`src/sim/Order.cs`, execution in `src/world/MachineSystem.cs`)

> **This is M5's central interaction, and the governing rule is that nothing
> here ever substitutes different work.** A vehicle runs exactly the order it
> was given, forever, and a blocked vehicle waits and reports why instead of
> going looking for something else to do. `#7`'s tracker names the failure
> mode this exists to avoid: the player must always be able to tell *why* an
> idle vehicle is idle.

**Data and execution are split the way machine state and drawing are.**
`Order`/`OrderKind`/`OrderStep`/`OrderBlock` are plain data in `src/sim/` —
ints and an enum, no dependency on the world, trivial to hash and to save.
Running one needs `WorldGrid` (road queries, `GetField`/`GetStructure`) and
`CropSystem`, so execution lives in `MachineSystem` for the exact reason that
class already gives for living in `src/world/` rather than `src/sim/`: it is
the thing that already owns the road queries a vehicle runs on. A second
class was rejected — see the next point.

**The order lives on the machine row, not a second registry.** Exactly the
shape `Fleet` already chose for crew: a registry keyed by `EntityId` would be
a third thing to keep level with two entity stores that recycle their slots,
and the question asked every tick is "what is *this* vehicle running", which
has to be O(1) off the row it is already ticking. `SetOrder` writes a queue of
one (`_pendingOrder`/`_hasPendingOrder`, two columns because "nothing queued"
and "queued to go idle" are different states and a lone `Order?` cannot tell
them apart); `AdvanceOrder` is the only thing that ever promotes it.

**"Next cycle, not mid-cycle" is one predicate: `committed`.** A vehicle is
mid-commitment while a route is still in flight or while it is holding cargo
— the two ways a vehicle can be partway through something physical — and a
queued order waits behind either, checked once at the top of every tick.
Neither is true of a vehicle that arrived and is simply blocked (wrong stage,
nothing to load, no driver): that is standing still, not a commitment, so a
re-point there takes over the very next tick. This reads as two different
behaviours from the player's chair — "finish the delivery already on the
truck" and "redirect it instantly, it hadn't done anything yet" — from the one
rule, which is the point: no order kind gets its own bespoke cutover logic.

**Trap: "arrived" has to mean the route is empty, not that the cell matches.**
The first cut compared `_cell[i]` — the coarse, discrete cell a continuous
position rounds to — against the target and started the field or haul work the
tick they matched. A vehicle enters the destination cell slightly before
`Drive` finishes steering it there, so that read "arrived" a tick or two early
while `_routeNext` still had one short waypoint uncounted. Ploughing and
unloading never noticed, since those only need the cell; the `committed` check
above did, because a nonempty route reads as mid-commitment, so a re-point
queued in that window sat un-promoted forever. Fixed by folding both tests into
one `HasArrived`: `_routeNext[i] >= _route[i].Count && _cell[i] == target`.
Caught by the smoke test's mid-haul re-point section, not by inspection.

**`OrderStep`/`OrderBlock` are recomputed every tick and never hashed.** Both
are pure functions of the order plus already-hashed state (cargo contents,
current cell, whether a route is in flight); persisting them would be a second
copy of information the save already carries, the same reason the route itself
is derived and unsaved. A load is correct again within one tick.

**Validating a vehicle kind reads exactly the two facts `MachineSpec` exposes
for it** (`WorksFields`, `CargoCapacity > 0`) — see that class's own doc
comment for why nothing finer exists. The consequence worth stating plainly: a
harvester *can* be given a plough or sow order, and a tractor can be ordered to
haul if some future spec gives it capacity. Nothing in the roster distinguishes
"drives a plough" from "cuts a header" today, and inventing that distinction
here would be exactly the taxonomy `MachineKind` was kept free of. `#34` only
refuses what the roster can already answer: can this thing work a field at
all, can it hold anything at all.

**A vehicle "arrives" at the road cell beside a field or building, never by
entering its footprint.** `WorldGrid.FindRoadAccess` walks a footprint's cells
for the first road neighbour, in footprint order, so the answer never depends
on a dictionary walk. A `Field` is legal with no road frontage at all — nothing
required one when it was marked — so `null` here is `OrderBlock.NoRoadAccess`.
`#36` folded the *other* way "nowhere to go" happens — frontage that exists but
that `FindRoadPath` cannot reach from wherever the vehicle sits — into that
same value rather than a second one: from the seat of the vehicle a missing
neighbour and an unreachable one read identically. Entering a footprint, lane
discipline and several vehicles sharing one door stay out of scope; nothing
has asked for them yet.

**A vehicle with no driver runs no order**, and "no driver" is the *resolved*
answer: `MachineSystem.CrewOf` is deliberately raw and can hand back somebody
who has since been let go, so execution asks `WorldGrid.Labour` whether the
handle is still alive, exactly as `Fleet.DriverOf` does. *Trap:* believing the
raw handle leaves a dismissed worker's tractor ploughing on by itself while
`Fleet.IsCrewed` reports the cab empty — the sim doing unpaid work, which is
the inverse of what this milestone demonstrates, and it costs one export to
avoid. Resolving on read rather than sweeping the handle is the rule the tick
loop's ownership already sets: a read never writes sim state. A scene with no
`Labour` wired believes the handle as written, the same "no such thing here"
reading a tool without an `Economy` has, so a dev scene can put a fabricated
crew in a cab.

**A haul's endpoints are two `Structure` ids, never a field.** A field's
harvest still lands in the field's own output buffer (`## Crops`), and nothing
in `#34` moves it from there into a building — that seam is deferred to
whichever of `#37` (silo), `#38` (depot) or `#39` (the loop test) first needs
grain to leave a field. A load or unload takes whatever one
`ItemBuffer.Transfer` moves in a single call, bounded by capacity on both ends,
rather than topping up over several ticks: nothing yet produces goods fast
enough for the difference to matter, and a policy for "wait for a fuller load"
is a decision nobody has asked for yet.
