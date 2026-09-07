## Not yet implemented (deliberate)

- **Nothing moves an item, and no machine works a field.** Every carrier has a
  hold and there is one call that moves a good between two of them; what nobody
  has written is the *deciding* — the orders and the silo, later in M5.
- **Structures have no behavior** beyond holding what is put in them, and there
  is one generic kind; the roster is M5's (silo) and M6's (cleaner, mill,
  bakery). They are also the last placed thing still a plain object in
  `WorldGrid`'s registries rather than an entity row.
- **Player interaction is the palette, four tools, the time bar and the field
  panel**, plus three dev keys — nothing hires. The unlock *seam* exists
  (`ToolAvailability`) and none of the rules — M8's; the HUD pass is M10's.
- **Nothing puts money in.** Wages take it out daily
  ([`## Labour and wages`](labour.md)); the credit and refund seams exist, pay
  nothing, and are M7's.
- **Nothing stops a road being bulldozed out from under a machine** driving it,
  and nothing re-checks a building's road access when the road beside it goes.
  Both are M5 cases; the notes sit on `WorldGrid.Clear`, where M5 will be.
