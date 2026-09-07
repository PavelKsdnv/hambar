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
- **Found by group, not `NodePath`**: entities are instanced at runtime. It
  ticks at `ProcessPriority = -100`, so a transform read this frame is the
  sim's.
- **Registration during a tick is unsupported** — an entity that wants to stop
  goes idle inside its own `Tick`.

### Entity storage (`src/sim/EntityStore.cs`, `EntityId.cs`, `SpatialHash.cs`)

Sim entities are rows in flat arrays, not nodes. What forces it is not frame
cost but **walkability** (`### State hashing`): a scene subtree gives an order
that depends on spawn history, plus an allocation per entity.

- **`EntityStore` owns liveness and nothing else**, each system keeping its own
  parallel arrays. No component registry, no archetypes, no queries — tech.md
  asks for a data-oriented *layout*, not an ECS framework, and generalising
  before M5 has said what components exist would be guessing.
- **The generation is the point of the handle.** Slots are reused, so an index
  alone cannot tell "the machine I spawned" from whatever moved into its slot
  after it died; that bug reads as teleportation. `Destroy` bumps the slot's
  generation, invalidating every stale handle at once, and live generations
  start at 1 so `default(EntityId)` is dead though slot 0 is real. A recycled
  slot still holds the old values, so a spawn must write **every** column.
- **Walk by slot, never by dictionary.** `SlotCount` + `IsAliveSlot` is the
  only iteration order offered: ascending, independent of creation order.
  Enumerating a `Dictionary` is the quiet way to lose determinism.
- **`SpatialHash` is a bucket of ids per grid cell** — keyed by cell because
  `WorldGrid` already owns the rounding rule, and a second granularity would be
  a second rule to keep in step. *Derived*: rebuildable, never saved, never
  hashed. Queries walk an ascending cell range rather than enumerating buckets,
  and emptied buckets are dropped or a roaming mover grows it to map size.

### State hashing (`src/sim/SimStateHash.cs`, `StateHash.cs`, `IHashableState.cs`)

> **Determinism is a claim nobody can eyeball, so it is a number.**
> `SimStateHash.Of(sim)` walks all sim state into 64 bits, taken after *every*
> tick of two runs from one seed — one hash at the end answers only "did they
> diverge", and bisecting by hand is the afternoon this prevents. M10 takes the
> same number either side of a save/load, hence a `Simulation` argument and
> nothing test-shaped.

**Order independence is the whole difficulty**: two runs holding identical
state can enumerate a `Dictionary` differently, so hashing that walk in order
reports a divergence that is not one. Entities go in by slot walk, streams by
`RandomStreams.Ordered`, anything else by a commutative *add* of mixed member
hashes plus a count — xor cancels a member against its duplicate.

**State hashes itself** (`IHashableState`) rather than a walker reaching into
everyone's arrays, so the milestone that adds a column adds one line where it
is already editing.

**Left out on purpose.** Speed, the tick accumulator and `DroppedTicks` are
real-time quantities — hashing them fails the harness on a slow frame, which is
how a determinism check gets switched off. Derived state (routes, the spatial
hash, cell → owner lookups) is out because M10 will not save it and a correct
rebuild would then fail the comparison; the cost is a *different* route showing
one tick late. Floats go in **by their bits**: rounding hides the drift.

**Trap for M10:** the entity free list is not hashed, only *which* slots are
free. A load rebuilding it in another order hashes equal today and hands out
different slots tomorrow, so restore it in destruction order.
