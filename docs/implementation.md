# Implementation notes

What exists in the codebase today and how it fits together. Companion to
`tech.md` (the engine decisions) — this documents how those were realized, and
the design decisions taken since.

**One file per subsystem, and each is written to be read *alone*** — find it in
the index below, read that file, stop. A file holds only what reading `src/`
does not recover: why a thing is shaped as it is, what was rejected, what is
deferred, and the traps. No member lists, no narration of what a test asserts,
no restating what the code plainly says.

**Budget: ~150 lines per file, ~120 per section.** A file that outgrows it gets
cut back or split in the same commit, never appended to. The budget is per file
on purpose: adding a subsystem must not force edits to anyone else's notes, and
a diff that touches one file is a diff that can be reviewed.

Last updated: 2026-09-07.

## Index

| File | Read it when |
|---|---|
| this file | building, running, or looking for where something lives |
| [`implementation/simulation.md`](implementation/simulation.md) | touching the tick, sim state, entity storage, or chasing a determinism bug |
| [`implementation/items.md`](implementation/items.md) | carrying, storing, moving or counting a good |
| [`implementation/calendar-random.md`](implementation/calendar-random.md) | touching days, seasons, game speed, pause — or drawing a random number |
| [`implementation/world-grid.md`](implementation/world-grid.md) | touching world state, terrain, tiles, roads, how a cell draws, or screen-to-cell |
| [`implementation/crops.md`](implementation/crops.md) | touching farmland, the crop lifecycle, growth or the yield |
| [`implementation/machines.md`](implementation/machines.md) | touching vehicles or movement |
| [`implementation/orders.md`](implementation/orders.md) | touching what a vehicle is told to do, or why a blocked one is idle |
| [`implementation/labour.md`](implementation/labour.md) | hiring, the payroll, or anything that costs per day |
| [`implementation/build.md`](implementation/build.md) | adding or changing a placement tool or rule, the toolbar, buildings, removal, prices |
| [`implementation/ui.md`](implementation/ui.md) | touching the camera, a dev key, the hover readout or a player-facing panel |
| [`implementation/dev-tests.md`](implementation/dev-tests.md) | writing or fixing a test — **read before writing one** |
| [`implementation/deferred.md`](implementation/deferred.md) | before assuming something is missing by accident |

## Building & running

- **Godot 4.7 stable (.NET/mono build)**, C# on **.NET SDK 8**. The
  `.csproj`/`.sln` are hand-written: `--build-solutions` will not create them
  from scratch.

## Project layout

The namespace is flat `Arable` throughout, so moving a file between folders is
free. `dev/` is test scenes, not the game; `scenes/Main.tscn` is the entry point.

**Gotcha (hand-written .tscn):** a Node-typed export serialized as
`World = NodePath("../World")` only resolves if the `[node]` header also carries
`node_paths=PackedStringArray("World")`; without it the property loads as null.
