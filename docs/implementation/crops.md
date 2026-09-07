## Fields (`src/world/Field.cs`, `src/ui/build/FieldBuildTool.cs`)

> **Farmland is addressed by one `Field` entity per marked rectangle — never by
> the cell.** Jobs and yields hang off the entity, reached with
> `WorldGrid.GetField`; what a crop is doing on it is one step further out
> again (`## Crops`).

`Field` ids are creation order and never reused. What that commits us to:

- **Exactly one owner per cell**, which is why the tool opts into `VacantCell`:
  a rectangle can never be drawn over ground another field holds, even partly.
- **Touching fields are never merged**, because each is separately named,
  worked and harvested. So **enlarging a field means marking another one**.
- **Road access is deliberately not required** to mark a field: nothing works
  one until M5 gives machines orders, which is when the rule (if any) belongs.

## Crops (`src/sim/CropSystem.cs`)

> **A field is a state machine, and the interesting half is what it refuses.**
> Ploughing, sowing and harvesting are *work*, sprouting and ripening are
> *time*, and `Apply` is the one door either kind takes. A refusal is its
> return value, not a warning: M5 will ask "can this be sown" far more often
> than it sows, and a line per refusal buries the log the first time a
> scheduler polls. Wheat only, and no crop-kind column — a one-valued enum is a
> member every later milestone keeps in step for nothing.

**Crop state left `Field` for the entity arrays**
([`### Entity storage`](simulation.md)). An enum and a float on `Field` would
have hashed fine through `WorldGrid`'s registry fold — and left the growth
factors, the output buffer and M5's jobs walking a `List` whose order a load
need not reproduce. The pairing is hashed from both ends, because neither side
derives the other.

**Harvest leaves stubble, to be ploughed in again.** Rejected: returning the
field straight to sowable, which makes ploughing a once-per-field job when M5
exists to program a *repeating* round — hence `StubbleNeedsPloughing`.

**Growth accumulates in whole ticks, not days**: adding 1/600 of a day a tick
lands a hair either side of the threshold in single precision, so a crop ripens
a tick early or late by whichever way the last rounding fell — invisible, and
untestable. A full-rate tick banks 1; the factors are fractions of it.

**A tick banks `base × fertility × season × water`, and multiplying is the
point.** A factor at zero *stalls* the crop where it stands instead of slowing
it, which is what stops an untended field quietly finishing and keeps M5's
labour worth programming; summing would let three good ones carry a zero. Every
factor is an `[Export]` on `WorldGrid`: M4's question is the cadence, and no
answer should need a rebuild. Shipped seasons run full, full, half, **nothing**
— winter at 0 makes *when* to sow a decision, and is the first number to soften
if the dead season plays long. **Water is inert**: nothing in the POC irrigates
or rains, but dropping the term makes whoever adds rain re-open product and
hash.

**Fertility reaches the row as a column, never as a lookup** — `src/sim/` knows
nothing of cells — and the number is the mean of the field's **chunk** means, a
chunk being a `FertilityChunkSize`-cell square. Rejected: the plain cell
average, which lets a field lean its rate on whichever patch it clipped most
of, so the same two patches answer differently depending where the drag
started; equal weight per chunk makes the rate a property of the ground the
field spans. Chunks align to the world origin, so the same ground falls in the
same chunks whoever marks it, and size 1 is the exact cell mean — the knob's
off position. The season is read live off `GameCalendar` instead: it is sim
state, and a copy taken at startup is wrong the first time a save loads in
autumn.

**A harvest deposits a stack into the field's own buffer**
([`## Items and buffers`](items.md)), reached through the crop row like the
stage, and takes that section's atomic door — **refused whole** when the yield
will not fit (`OutputFull`), leaving the crop standing ripe, because a partial
fill would need a half-cut field the state machine has no stage for. M4's only
backpressure, and M5's collection clears it. **Yield is the ground, not the
growth banked** — `YieldPerCell × area × fertility`, floored — because banked
growth stops at ripe and is the same for every field that finished, while the
ground pays fertility twice: good soil ripens sooner *and* cuts heavier. Season
stays out, so winter delays a harvest instead of shrinking one;
`ProjectedYield` is that same sum ([`## Field panel`](ui.md)). **Area is a
second pushed-down column** sizing the buffer at `FieldOutputHarvests`
*perfect* harvests, so only an uncollected second cut bites.

**Traps.** A row takes its ground at `MarkField` and whenever the field's cells
change, never per tick — so moving `FertilityChunkSize` at runtime
re-aggregates fields marked *after* it, not those already standing. The size is
frozen onto the `Field` at marking and re-read from there; without that, the
live export would re-chunk a field at a granularity it was never marked at the
moment one corner of it was bulldozed. Neither the multiply order nor the chunk
walk may be reordered (float arithmetic is not associative), which is why the
walk sorts the cells by (chunk row, chunk column, input index) and never
enumerates a dictionary — the index breaks every tie, so summation order is a
function of the cells alone. Sorted rather than bucketed into an array over the
chunk bounding box, which it did first: cells off the map are legal, so two
far-apart ones size that array by the *gap* — an unbounded width × height that
wraps negative first. The factors hash beside the thresholds, or two
differently tuned worlds hash alike. And the harvested good is configuration,
not a column, exactly as the crop kind is — both become columns the day a
second crop lands.
