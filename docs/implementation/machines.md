## Machines (`src/world/MachineSystem.cs`, `src/world/Machine.cs`)

A machine is any vehicle. **State and drawing are two objects**: rows in
`MachineSystem`'s arrays ([`### Entity storage`](simulation.md)), drawn by a
`Machine` node that is an `ISimView` and nothing else. `WorldGrid` constructs
and registers the system, because machines run on the road queries it already
owns.

> **Nothing in here looks for work.** The tick spends a travel budget along a
> route somebody else put there, and an empty route is the ordinary, indefinite
> state of a vehicle rather than a fault. `#34`'s orders are the only thing
> that ever fills one — a vehicle nobody has given an order to is still idle,
> which is the point M5 exists to demonstrate.

**Wandering is gone, not disabled.** A machine used to pick a random road cell,
BFS to it and repeat forever. That was dev scaffolding, and it hid the one
thing the milestone has to be able to show: that a farm only does what it was
told to. The movement half is kept whole — the travel budget, the smoothing,
the road queries — because orders supply a route and this is what drives one.
The *test* was inverted rather than deleted, for the same reason: "it moved on
its own" is precisely the regression somebody adds later while being helpful.

- **The node transform is a drawing, not a position.** `Interpolate` writes it
  each frame from the row's last two sim poses; `SimPosition` is where the
  machine is. The exports are spawn *input*, read once off the instanced scene
  and copied into the arrays — changing one on a live node does nothing.
- **Freeing the node despawns the row.** `Machine._ExitTree` is the one place a
  view touches sim state — lifecycle, not a state write. Nothing else notices a
  freed node, and an orphan row is an invisible machine still standing on the
  road with a driver assigned to it.
- **Determinism: the system no longer draws a random number at all.** An
  authored order is not a roll, so the stream went with the wandering. Where a
  bought vehicle *parks* is still random, but that is the world laying the game
  out and comes off `WorldGrid.SpawnStreamName`.

**One scene, three kinds** (`MachineKind`, `MachineKinds`). A kind is applied by
writing the scene's exports (`Machine.ApplyKind`) rather than by reading a table
at spawn time, so spawn input still comes through exactly one door and a
hand-placed machine in a dev scene stays inspector-tunable.

**The roster is a static table, not fifteen exports.** Every other tunable is an
export because a playtest argues about it alone — one balance, one wage. Three
kinds times five numbers is not a knob, it is content: the numbers only mean
anything relative to each other, and "a truck is faster than a tractor" is a
fact about the roster rather than about the truck. It is in code because there
is no data-file pipeline yet and building one for three rows is the wrong
milestone; M6 adds a roster of *buildings* with the same shape, and both should
become data there.

**A kind carries no order list.** The two facts an order system needs to refuse
a mismatch — can this work a field, can it hold anything — are a `WorksFields`
flag and a cargo capacity that is **zero for a vehicle that cannot haul at
all** (a tractor pulls implements). A half-guessed enum of order names here
would be a taxonomy the order issue then has to argue with. *Trap:* zero is a
real capacity, so a test that wants to prove a hold works must use a truck.

**Parking uses one draw and a forward walk**, wrapping, rather than retrying
random cells until one is free. Retrying makes the *number* of draws depend on
how full the road is, so two runs that parked the same vehicles in the same
places would leave the stream in different positions — a determinism trap that
would surface far from its cause.

## The fleet (`src/world/Fleet.cs`)

> **Buying a machine and putting somebody in it are the two acts the player
> performs, and neither is ever performed for them.** A vehicle with no driver
> and a worker with no vehicle are both ordinary states; the farm simply does
> less. The moment this node paired an idle worker with an idle truck
> "helpfully", the player would stop being able to tell what they asked for
> from what the game decided, and every later issue would argue with it.

**A node of its own**, not methods on `WorldGrid`: the world places things and
owns no money, while both acts here need an account and assignment needs the
labour pool as well. That is the shape a `BuildTool` already has, and the same
reason [`## Labour and wages`](labour.md) is a node — the wiring is scene
wiring, and a null reference means "this scene has no such thing" rather than
an error. A fleet with no `Economy` buys for free, exactly as a tool with none
builds for free.

**It holds no state.** The worker↔vehicle link lives on the machine row, so it
is saved, hashed and invalidated with the vehicle. A registry here instead
would be a third thing to keep level with two entity stores that both recycle
slots. The link is stored on the *vehicle* side because the question asked
every tick, from `#34`'s order execution onward, is "does this one have a
driver", which has to be O(1) on the row; the reverse lookup is a walk over a
handful of slots and needs no index.

**Dangling handles are resolved on read, never swept.** Letting a driver go
leaves their handle on the vehicle they drove, and nothing goes looking for it:
`DriverOf` asks the pool whether that worker is still alive and answers nobody
if not, so the cab is immediately free to crew again. A re-hired worker gets a
fresh generation, so an abandoned handle can never match them. A cleanup pass
would be a sim state write triggered by a read, which the tick loop's ownership
rules forbid.

**Buy order is deliberate: check, place, then charge.** Placing before charging
is what makes "nowhere to park" a free refusal; charging first and refunding on
a failed placement would put a refund path into a purchase for a case that is
not an error.

**Orders land in `#34`** (`## Orders`) as columns on this same row rather than
a second registry, and their execution is what finally fills the route these
two systems were always kept ready for — read that file for what a vehicle is
told to do and why a blocked one reports the reason it does.

**Still deferred.** The dropped *"the view draws poses between ticks"*
assertion has not come back: `#34`'s smoke test drives the sim through
`Simulation.Step`, which explicitly does not run the frame loop
`Machine.Interpolate` belongs to (see `## Simulation`), so it proves orders
move a vehicle without proving the view blends it smoothly between ticks. That
still wants a *played*, frame-driven test — `#35`'s HUD, or the screenshot
test, are the more natural place to look at a machine than a headless one is.
