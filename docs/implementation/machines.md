## Machines (`src/world/MachineSystem.cs`, `src/world/Machine.cs`)

A machine is any vehicle. **State and drawing are two objects**: rows in
`MachineSystem`'s arrays ([`### Entity storage`](simulation.md)), drawn by a
`Machine` node that is an `ISimView` and nothing else. `WorldGrid` constructs
and registers the system, because machines run on the road queries it already
owns.

- **The node transform is a drawing, not a position.** `Interpolate` writes it
  each frame from the row's last two sim poses; `SimPosition` is where the
  machine is. The exports are spawn *input*, read once off the instanced scene
  and copied into the arrays — changing one on a live node does nothing.
- **A machine carries a load** it cannot yet fill or empty
  ([`## Items and buffers`](items.md)): the column and its place in the hash
  are here so #34's orders add the *moving*, not the storage, and a truck that
  hauled shows in the hash.
- **Behavior:** wander. Smoothing keeps a stair-stepped diagonal road from
  being driven as a zigzag. A tick spends a travel budget (`Speed·dt`) across
  waypoints so corners lose no distance, and the previous pose is snapshotted
  for *every* machine — a parked one drifts.
- **Freeing the node despawns the row.** `Machine._ExitTree` is the one place a
  view touches sim state — lifecycle, not a state write. Nothing else notices a
  freed node, and an orphan row is an invisible machine still driving the
  roads.
- **Determinism:** one named stream for the whole system
  ([`## Randomness`](calendar-random.md)), drawn in slot order. Spawn placement
  comes off the *world's* stream instead, so spawning a machine cannot reroute
  the ones already driving.
