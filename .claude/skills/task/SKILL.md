---
name: task
description: Capture a feature/change discussed in chat as a GitHub issue in this repo, written as a self-contained brief for a future agent session and labelled needs-review. Use whenever the user says "write this down as a task", "make an issue for that", "file that", "add it to the backlog", or otherwise asks to record something discussed rather than build it now.
---

# Capture a task as a GitHub issue

The user reviews and edits tasks **on GitHub**, not in chat. Publish first, hand
back the link, stop.

## Rules

1. **Publish without asking.** Do not preview the title/body in chat for
   approval, do not ask "shall I file this?", do not offer to adjust wording.
   The user has durably authorized this — they will edit on GitHub. This
   overrides the usual confirm-before-outward-facing-action default.
2. **Always apply `needs-review`.** That label is the signal that a human has
   not vetted the issue yet. Add topical labels too when one clearly fits —
   check `python scripts/gh_issues_read.py labels` if unsure what exists.
3. **One issue = one agent session.** If what was discussed is bigger than that,
   split it into several issues rather than filing one large one (see Scoping).
4. **Report back in one or two lines**: the number, title and URL. No summary of
   the body you just wrote — the user is about to read it on GitHub.

## How to publish

Write the body to a file in this session's scratchpad directory first —
multi-line markdown does not survive a shell argument on Windows — then publish
with `--body-file` pointing at it:

```bash
python scripts/gh_issues_publish.py create \
    --title "Stream GridMap chunks around the camera" \
    --body-file <scratchpad>/task-body.md \
    --label needs-review,enhancement
```

Add `--dedupe` if there is any chance the same task was already filed.
For several issues at once, build a JSON list and use `batch --dedupe`.
See `scripts/README.md` for the full flag set.

## Writing the issue

The reader is an agent starting a **fresh session with no memory of this
conversation**. It has the repo, `CLAUDE.md`, and `docs/`. Everything else it
needs must be in the issue. Carry over the decisions and constraints that were
settled in the discussion — that reasoning is the part that would otherwise be
lost.

**Title**: imperative and specific, ~70 chars max. "Stream GridMap chunks around
the camera", not "GridMap improvements".

**Body** — use this shape, dropping sections that would be empty:

```markdown
## Goal
One or two sentences: what should be true when this is done.

## Context
Why this came up and what was decided in the discussion — constraints, rejected
alternatives and the reason they were rejected. Link the doc sections that
govern it (`docs/tech.md`, `docs/implementation.md`).

## Where
Concrete starting points: files, scenes, classes. `src/world/WorldGrid.cs`,
`scenes/world/World.tscn`. Say which are new vs. existing.

## Approach
The sketch that was agreed, if one was. Where a choice was left open, say so
explicitly and name the options — do not silently invent a decision the
discussion did not reach.

## Done when
- [ ] Observable, checkable outcomes — not "implement X".
- [ ] `python scripts/verify.py` passes (build, every smoke test, doc budget).
- [ ] Name the smoke test that actually covers this, or say a new one is needed
      — `verify.py` picks up any new `scenes/dev/*SmokeTest.tscn` on its own.

## Out of scope
What this task deliberately does not cover, and the follow-up issue number if
it was split out.
```

## Scoping

Sized right for one session: a coherent change across a handful of files, with
an end state a smoke test or a build can confirm.

Split when the task would need a new subsystem *and* its consumers, or when
"Done when" grows past ~5 items, or when parts can land and be verified
independently. When splitting, file the issues in dependency order and
cross-reference them by number in **Out of scope** (file the prerequisite first
so you have its number).

Do not pad a small task to fit the template — a three-line Goal plus Done when
is a perfectly good issue.
