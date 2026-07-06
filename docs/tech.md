## Decision: Godot 4 (.NET build)

Engine is **Godot 4**, using the **.NET build** so the game can be written in **C#**.
The plain "Godot Engine" build only supports GDScript/C++ — it can't run C#. The
".NET" build bundles the runtime that makes C# work (and can still use GDScript too).

**Prerequisites**
- Download the **Godot Engine – .NET** build (the one labelled "C# support").
- Install the **.NET SDK 8** (or current LTS) separately — Godot ships the runtime
  but needs the SDK to build C# projects.

## Rendering & camera

- **Look:** 3D models rendered to read as isometric — not hand-drawn 2D sprites.
- **How:** a `Camera3D` set to *orthographic* projection at a fixed iso angle.
- **Grid:** use Godot's built-in `GridMap` (snaps meshes from a `MeshLibrary` to a
  3D grid) for grid-placed structures and roads — placement, snapping, and cell
  coordinates come for free.
- **Terrain:** built-in `FastNoiseLite`.
- **Camera controls:** pan + zoom + 90°-step rotate. (True "tilt" is unusual for this
  genre; a fixed pitch is standard.)

## Simulation architecture (the part that matters most)

The performance of an automation game lives in the sim loop, not the rendering.
Regardless of engine specifics:

- Run the simulation on a **fixed timestep**, fully **decoupled from rendering**
  (sim ticks at a steady rate; the view just draws the latest state).
- Use a **data-oriented / ECS-like layout** for entities.
- Use a **spatial grid / hash** for fast entity lookups.
- Keep the sim **deterministic** → saves become trivial (serialize state), and you
  get replay/debugging for free.
- For roads/logistics with many movers, prefer **flow fields** over per-agent A*.

## Useful libraries / ecosystem

- **gdext (godot-rust)** — Rust bindings, for offloading the simulation hot path.
- **Chickensoft** — Godot C# ecosystem: LogicBlocks (state machines), dependency
  injection, and save/serialization patterns well suited to a sim game.
- Built-in: `GridMap`, `FastNoiseLite`, `Camera3D` (orthographic).

---
*Game context: Arable — agricultural automation game (Factorio-like) with realistic
production chains and a dynamic market. Top/iso view, grid-based, PC-first.*