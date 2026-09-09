## Not yet implemented (deliberate)

- **Road removal is still not a safe operation.** A road cell can be cleared
  out from under a vehicle driving over it, or out of a route already planned
  and in flight, and nothing re-paths or refuses; likewise nothing re-checks a
  building's or a field's road access when the frontage beside it goes. The
  notes sit on `WorldGrid.Clear`, which is the function that would hand a
  machine the news. M5 gave vehicles routes and did not close this.
- **A stranded load has nowhere to go.** A truck holding cargo for a building
  that was demolished under it can be re-pointed or stopped
  (`MachineSystem.CanStillDeliver`), but the load stays in the hold until some
  later order's delivery leg happens to carry it out. Dropping it, refusing the
  demolition, or returning it to source are all unwritten.
- **Structures still do not *do* anything.** Two kinds: a silo holds what is
  hauled in, a depot sells it at a flat `Economy.PriceOf`. Nothing transforms
  anything — the cleaner, mill and bakery are M6's, and so is the panel that
  would show a building's contents (`## Hover readout`'s trap explains why the
  cell readout is not that panel). Buildings are also the last placed thing
  still a plain object in `WorldGrid`'s registries rather than an entity row.
- **There is no market.** `Economy.PriceOf` is a constant per item type and
  `Economy` is the named swap point for M7's live series; nothing moves a
  price, and there is one good in the catalogue to price.
- **No unlock rules and no HUD.** The palette's `ToolAvailability` seam exists
  and nothing drives it (M8); the money readout is a label, and the HUD proper
  is M10's.
