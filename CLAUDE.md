# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**Arable** — an agricultural automation game (Factorio-like) with realistic production chains (gather → clean → refine → transform → sell) and a dynamic market that decides which chain is worth running. Top/iso view, grid-based, PC-first. Design docs live in `docs/`:

- `docs/arable-concept.html` — game concept map (core loop, systems)
- `docs/tech.md` — engine/tech decisions (read this before making architectural choices)
- `docs/implementation.md` — what's implemented so far and how (project layout, camera, world grid, machines, smoke tests); keep it updated as features land

## Engine & toolchain

- **Godot 4 (.NET build)** — game code is written in **C#**; requires the Godot ".NET"/C#-support editor build plus a separately installed .NET SDK (8 / current LTS).
- Rendering: Forward Plus, D3D12 on Windows; physics: Jolt.
- `dotnet build Arable.sln` compiles the game assembly (`Arable.csproj`/`Arable.sln` are hand-written; Godot's `--build-solutions` doesn't create them from scratch).
- Verify changes with the headless smoke tests: `godot --headless --path . res://scenes/dev/CameraSmokeTest.tscn` (and `WorldSmokeTest.tscn`) — they print PASS/FAIL and exit 0/1.
- Run the game from the CLI with the Godot .NET editor binary: `godot --path .` (add `--headless` for editor-less operations like `--build-solutions` or `--import`).
- `.godot/` is generated cache — never edit or commit-worthy content lives there.

## Architecture decisions (from docs/tech.md)

These are settled decisions; follow them rather than re-deciding:

- **Isometric look via 3D**: `Camera3D` with orthographic projection at a fixed iso angle — real 3D models, not 2D sprites. Camera: pan + zoom + 90°-step rotation, fixed pitch.
- **Grid**: use Godot's built-in `GridMap` + `MeshLibrary` for grid-placed structures/roads (free placement, snapping, cell coords). Terrain from `FastNoiseLite`.
- **Simulation** (the performance-critical core):
  - Fixed timestep, fully decoupled from rendering — the view only draws the latest sim state.
  - Data-oriented / ECS-like entity layout; spatial grid/hash for lookups.
  - Keep the sim **deterministic** — saves are state serialization; enables replay debugging.
  - For logistics with many movers, prefer **flow fields** over per-agent A*.
- Ecosystem candidates: **Chickensoft** (LogicBlocks state machines, DI, save patterns) for C#; **gdext (godot-rust)** if the sim hot path needs offloading.
