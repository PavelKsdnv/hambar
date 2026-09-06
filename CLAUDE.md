# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**Arable** — an agricultural automation game (Factorio-like) with realistic production chains (gather → clean → refine → transform → sell) and a dynamic market that decides which chain is worth running. Top/iso view, grid-based, PC-first. Design docs live in `docs/`:

- `docs/arable-concept.html` — game concept map (core loop, systems)
- `docs/tech.md` — engine/tech decisions (read this before making architectural choices)
- `docs/implementation.md` — how what exists is shaped and *why*: the decisions, rejected alternatives, deferrals and traps that reading `src/` does not recover. **Read it by section, not whole** — its `## Index` maps subsystem to section, and one reads out with `sed -n '/^## World grid/,/^#/p' docs/implementation.md`. Keep it updated as features land, and keep it to the durable half: no member lists, no narration of what a test asserts, no restating what the code plainly says. Budget is ~120 lines per section and ~700 for the file; a section that outgrows it gets cut back in the same commit, not appended to.

## Engine & toolchain

- **Godot 4 (.NET build)** — game code is written in **C#**; requires the Godot ".NET"/C#-support editor build plus a separately installed .NET SDK (8 / current LTS).
- Rendering: Forward Plus, D3D12 on Windows; physics: Jolt.
- `dotnet build Arable.sln` compiles the game assembly (`Arable.csproj`/`Arable.sln` are hand-written; Godot's `--build-solutions` doesn't create them from scratch).
- Verify changes with **all four** headless smoke tests — `godot --headless --path . res://scenes/dev/<name>.tscn` for `CameraSmokeTest`, `WorldSmokeTest`, `BuildSmokeTest` and `SimSmokeTest` (the determinism harness: two runs from one seed, hashed every tick, reporting the first tick they differ on). Each prints PASS/FAIL and exits 0/1; the older ones are the regression net for the newer ones.
- Check *visual* claims with `godot --path . res://scenes/dev/ScreenshotTest.tscn -- <dir>`, which renders the canonical views to PNGs you (or an agent) can actually look at. **Must run windowed** — under `--headless` the rasterizer is a dummy and there is no framebuffer to read back, so the scene detects it and exits 1 with `SCREENSHOT TEST FAILED: --headless cannot render`. Output dir defaults to `user://screenshots`; pass a scratchpad path to keep runs disposable.
- Run the game from the CLI with the Godot .NET editor binary: `godot --path .` (add `--headless` for editor-less operations like `--build-solutions` or `--import`).
- `.godot/` is generated cache — never edit or commit-worthy content lives there.

## Issue tracking

Tasks live as GitHub issues on the `origin` repo. Two stdlib-only Python scripts wrap the API — `scripts/README.md` has the full flag set:

- `python scripts/gh_issues_read.py list|get|comments|search|labels|milestones` — read side; `--json` (optionally `--fields`) for machine-readable output, PRs filtered out by default.
- `python scripts/gh_issues_publish.py create|batch|update|milestone|comment|close|reopen` — write side; `--dry-run` previews the payload, `--dedupe` keeps repeat runs idempotent. `milestone` upserts by title.
- Both resolve the repo from the `origin` remote and the token from `$GITHUB_TOKEN` / `gh auth token`, so `--repo`/`--token` are rarely needed.
- Caveat: GitHub's issue *list* endpoint lags a few seconds behind a write, so a `--dedupe` scan run immediately after a create can miss it.

When the user asks to write something down as a task or issue, follow the `task` skill (`.claude/skills/task/SKILL.md`): file a self-contained brief sized for a single agent session, label it `needs-review`, and publish without asking for approval in chat — the user reviews and edits on GitHub.

Each milestone also has one `[Mn] Tracker:` issue holding its acceptance criteria — an umbrella, not a session-sized task. Issues carry `needs-review` until a human vets them, then `reviewed` ("ready for development"); only `reviewed` issues are ready to be picked up.

When the user asks to *implement* a milestone, follow the `milestone` skill (`.claude/skills/milestone/SKILL.md`): it works the milestone's `reviewed` issues one at a time on a `milestone/<id>` branch, each in its own fresh subagent, with the driver re-running the build and smoke tests itself before committing and closing, and the tracker checked and closed last.

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
