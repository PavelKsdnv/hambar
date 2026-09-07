## Items and buffers (`src/sim/ItemType.cs`, `ItemBuffer.cs`)

> **A good is a type id and a quantity in a container with a capacity, never a
> counter.** A global grain total leaves M5's trucks nothing to collect from
> and M6 no way to tell grain from the flour it becomes.

**A type is a catalogue row, not an enum**, which would need editing — with
every switch over it — each time a recipe adds a good; an id indexes parallel
arrays (M7's price is another one). Ids are **stable**, appended and never
reordered, because a save writes the number.

**Capacity counts units, not stacks, and exists before anything consumes it**:
"the output is full, so production stops" is what M6's chains are made of.
Rejected: per-good limits, needing a rule for splitting a mixed buffer's room
that nothing has asked for. Contents are **never dropped to fit** a capacity
lowered under them, and a surplus is never spilled: both are grain evaporating,
which is the logistics game deleting itself.

**A move between two buffers is `Transfer`/`TryTransfer`, never a `Remove`
beside an `Add`** — a hand-written pairing is where the halves disagree about
how much moved, and the item lost that way surfaces much later as a hash
divergence with nothing pointing at the line. **The destination is credited
first and the source debited by what it accepted**: taking first and then
failing to deposit loses items, while this order only double-counts, across two
statements with nothing in between. Hence a buffer transferring to *itself*
answers 0 — add-then-debit would "succeed" by handing back its own room — and
one call moves one good, a mixed drain being the caller's priority decision.

**Every carrier holds the same type**: a field's crop row, a vehicle's `Cargo`,
a building's `Storage`, so a haul is one call whatever its ends are. Each is
sized from an export, and a truck's is deliberately under a grown field's so
clearing one is several trips. `#37` answered the question this used to defer:
a building's *single* store **is** the in/out interface, not half of a pair —
a silo deposits and withdraws through the same buffer, and a recipe machine
splitting it is a decision M6 still owns, not one a silo forced. A row takes a
**fresh** buffer at spawn, never the recycled slot's emptied out: a generation
invalidates a stale *handle*, not a reference to the object behind it.

**#38's depot is the one place a unit leaves every buffer for good.** A sale
calls `ItemBuffer.Remove` directly rather than `Transfer` — there is no second
buffer to move into, the unit is being converted to money, not carried — paired
with one `Economy.Credit` in the same call so nothing is ever mid-flight
between "in the world" and "in the account". Everywhere else "never dropped,
never spilled" holds unconditionally; this is the one deliberate exception, and
the reason it is not a third quiet way to lose an item is that it is paired
with a credit a determinism hash can also see.

**Deferred:** an item having a position of its own; until then a unit exists
only inside some buffer, which is what makes "nothing was lost" testable.
