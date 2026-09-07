## Not yet implemented (deliberate)

- **Nothing moves an item, and no machine works a field — or moves at all.**
  Every carrier has a hold, one call moves a good between two of them, and
  vehicles can be bought and crewed; what nobody has written is the *deciding*.
  Orders, the road driving that serves them and the silo are the rest of M5.
  A vehicle with no route stands still on purpose ([`## Machines`](machines.md)).
- **Structures have no behavior** beyond holding what is put in them, and there
  is one generic kind; the roster is M5's (silo) and M6's (cleaner, mill,
  bakery). They are also the last placed thing still a plain object in
  `WorldGrid`'s registries rather than an entity row.
- **Player interaction is the palette, four tools, the time bar and the field
  panel**, plus three dev keys. Hiring, buying and crewing are API-only so far;
  the panel that programs a vehicle is M5's last UI issue. The unlock *seam*
  exists (`ToolAvailability`) and none of the rules — M8's; the HUD is M10's.
- **Nothing puts money in.** Wages take it out daily
  ([`## Labour and wages`](labour.md)); the credit and refund seams exist, pay
  nothing, and are M7's.
- **Nothing stops a road being bulldozed out from under a parked machine**,
  and nothing re-checks a building's road access when the road beside it goes.
  Both are M5 cases; the notes sit on `WorldGrid.Clear`, where M5 will be.
