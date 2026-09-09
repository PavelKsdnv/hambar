## Game calendar (`src/sim/GameCalendar.cs`, `src/ui/TimeControls.cs`)

> **Two clocks, and conflating them is the trap.** `SimClock` is the *tick
> scheduler* — real seconds in, whole ticks out. `GameCalendar` is the *date* —
> ticks in, days and seasons out. Neither measures what the other does, which
> is exactly what lets the speed control move the first and never the second —
> and makes a day a count of ticks rather than of seconds, so the same ticks
> elapse per simulated day at every speed by construction, 3× only running them
> sooner.

**The numbers, and why.** A day is **600 ticks** — 30 s at 20 Hz, about one
haul across the map, so a five-minute playtest sees ten and day-scale effects
(M7's price drift, M8's deadlines) are observable in a sitting. 600 and the
**12-day** season factorise hard, so sub-day schedules and whole-day growth
stages land without remainder. Both are `[Export]`s; the season *count* stays
at four, because which seasons exist is content.

**The whole of the calendar's state is one `long`**; everything else is
division, because #25 must hash sim state and M10 must save it and an integer
is the cheapest thing either can walk. Day and year read **1-based** while
`TotalDays` is 0-based — mixing them is off-by-one in every printed date. And
there is deliberately **no rollover signal**: a system that cares keeps the day
it last acted on in its own state ([`## Labour and wages`](labour.md) first),
which saves and hashes with it, where an event would be re-fired by a load,
replay or date jump.

**Speed scales the schedule, never the step.** `SimClock.Speed` multiplies the
real time booked, so 2× runs twice the ticks per real second with `TickDelta`
untouched; a variable step would make a run depend on the speed the player
happened to watch at. Rejected: `Engine.TimeScale` (scales the *frame* delta,
so camera smoothing speeds up with the sim) and `SceneTree.Paused` (stops the
camera too — a paused world still has to be lookable-around).
`MaxTicksPerFrame` is deliberately *not* scaled with it, so at 3× it bites
below ~12 fps and the sim runs slow, visibly via `DroppedTicks` — never by
shortening a day. And **speed is not sim state**
([`### State hashing`](simulation.md)); the calendar is the opposite.

**The controls copy the palette** ([`## Build palette`](build.md)). **Step 0 is
always pause**: the ladder is otherwise free-form, but the code must find the
stop, so one not starting at 0 is repaired at load with a warning.
**Deferred:** a pause *key* (the number row is the palette's) and M9's seasonal
hazards.

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
xoshiro/xorshift, PCG wins the two points that matter: **every 64-bit value is
a legal state**, so a restore can never land in the all-zeros trap, and **the
increment is a stream selector**, so deriving it from the name gives a system
its own sequence rather than an offset into one shared cycle. Restoring assigns
its single `ulong` *mid-sequence*; `Reseed` means *new world*, not *load*.

**A class, not a struct:** a mutable value type advances a *copy* as soon as it
is passed or pulled out of a collection, surfacing as a determinism failure and
not as anything that looks wrong. Streams are per **system**, not per entity —
a slot walk is reproducible already — and their names live with the owning
system, not a central list; a typo therefore opens a *new* stream, hence
constants.

**Three traps.** `WorldGrid.WorldSeed` forwards to the seed on `Simulation`
(terrain is its loudest consumer, not its owner), but that `[Export]` is the
**authored** seed and `Streams.WorldSeed` the live one — a reseed does not
write back. Out of the tree there is no `Simulation` to forward to: a *read* is
silent (the throwaway registry answering it is dropped on entry), a *write* is
dropped with it and warns. **`FastNoiseLite` is not a stream** (one int in, its
own field out), so terrain takes its int from `DeriveSeed` rather than an
offset like `WorldSeed + 7919`: adjacent noise seeds are not guaranteed
unrelated. And **never `string.GetHashCode`**: randomised per process, it would
seed a different stream every launch, a bug reproducing in no single run — the
name hash is FNV-1a over the bytes, pinned to golden values in the smoke test.

**Deferred:** nothing writes a stream anywhere yet (M10).
