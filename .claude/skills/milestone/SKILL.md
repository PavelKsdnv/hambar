---
name: milestone
description: Work a whole GitHub milestone to completion — order its reviewed issues by dependency, implement each one in a fresh subagent session, independently verify the build and smoke tests, then commit and close, finishing with the milestone's tracker issue. Use when the user says "do milestone M5", "work the next milestone", "finish M2", or otherwise names a milestone to implement rather than plan.
---

# Work a milestone to completion

You are the **driver**, not the implementer. You order the work, hand each issue
to a fresh subagent, and independently verify what comes back. You do not write
game code yourself except in the repair pass (step 6).

State lives in **GitHub and git**, never in your context: an issue is done when
it is closed, and a run is resumable because re-invoking the skill simply picks
up whatever is still open. Keep it that way — nothing you learn may be needed by
a later step unless it was written to the repo, the issue, or a commit.

## 1. Preflight

Refuse to start, with a one-line reason, if any of these fail:

- **Clean working tree.** `git status --porcelain` is empty. Uncommitted work
  would get swept into some issue's commit.
- **Toolchain is green.** `dotnet build Arable.sln` succeeds *before* you change
  anything. A run must start from green or you cannot tell whose failure it is.
- **The milestone exists.** Resolve it with
  `python scripts/gh_issues_read.py milestones`, accepting a number, a title, or
  a prefix like `M5`. Note its number — `list --milestone` wants the number.

Then branch off the current HEAD and stay there for the whole run:
`git switch -c milestone/M5`. Never commit to `master`. Never push.

## 2. Read the tracker first

Every milestone carries exactly one **tracker issue**, titled `[Mn] Tracker: …`.
It is the milestone's acceptance spec, not a task:

- **Never hand it to an implementation subagent.** It is deliberately not
  session-sized.
- Read it first. Its `Done when` boxes are the gate the whole milestone is
  judged against, and its `Not in this milestone` names what to refuse.
- Close it **last** (step 7), after every box is verified.

**GitHub is the source of truth for the plan.** Some tracker bodies still carry
a footer citing `docs/poc-milestones.md` — that file is deleted and is not
coming back. Ignore the footer, never try to read or recreate the file, and
never treat its absence as a reason to stop.

## 3. The reviewed gate

`reviewed` ("ready for development") is the positive signal that a human vetted
the brief; `needs-review` means agent-drafted and unvetted. Building from an
unvetted brief burns a session and pollutes the repo, so this is a hard gate.

```bash
python scripts/gh_issues_read.py list --state open --milestone <number> \
    --json --fields number,title,labels,body
```

Split the open issues, tracker aside, into **ready** (labelled `reviewed`) and
**blocked** (everything else).

- No ready issues → stop. Report the blocked list with URLs and nothing else;
  the user reviews and edits on GitHub, not in chat.
- Some ready → work only those, and name the blocked ones in the final report.

The user may override per run ("ignore needs-review", "work them anyway").
Honour that when they say it; never assume it.

## 4. Order pass (read-only)

From the bodies you already fetched, derive an execution order:

- **`Out of scope`** sections cross-reference prerequisites by number — the
  primary edge source, because the `task` skill files split issues that way.
- **`Where`** sections name files. An issue that *creates* `src/sim/Foo.cs`
  comes before one that consumes it.
- Ties break toward the lower issue number.

Recompute this every run and never persist it — GitHub is the state, and a stale
plan file is worse than no plan.

Then sanity-check each brief against the repo **as it is now**, before any
subagent starts. Call out, briefly, any issue that is already implemented,
contradicted by code that has since landed, or too vague to have a checkable
`Done when`, and ask whether to skip it. Do not silently reinterpret a brief.

Report the order as one compact list, then start.

## 5. Per issue: implement in a fresh subagent

One subagent per issue, `subagent_type: "general-purpose"`. It gets the issue
number, not your conversation — the brief is self-contained by construction, and
the clean context is the point. Prompt it to:

- Read the brief: `python scripts/gh_issues_read.py get <n>`.
- **Read the `docs/implementation.md` sections covering the subsystems it is
  about to touch — by section, not the whole file.** That file is how previous
  issues hand over what they decided, and the rationale in it is the part not
  recoverable from the source. Its `## Index` maps subsystem to section, and one
  section reads out with
  `sed -n '/^## World grid/,/^#/p' docs/implementation.md`.
- Implement it, following `CLAUDE.md` and the settled decisions in
  `docs/tech.md` rather than re-deciding them.
- Make `dotnet build Arable.sln` succeed and every `scenes/dev/*SmokeTest.tscn`
  pass; write a new smoke test if `Done when` calls for one.
- Update `docs/implementation.md`. This is the **only** channel by which the
  next issue's agent learns what this one decided — treat it as required output,
  not bookkeeping. Two rules on *how*, because that file is read by every later
  agent and its size is a running cost:
  - **Write the durable half only.** Why the shape was chosen, what was
    rejected, what is deferred and to which milestone, and the traps that cost
    an afternoon. Not member lists, not what a test asserts, not a restatement
    of what the code plainly says — that is derivable, and it goes stale
    silently.
  - **Stay inside the budget: no section over ~120 lines, and the file under
    ~700.** Adding a feature is not licence to append. If a section has outgrown
    that, cut it back in the same commit — usually by deleting inventory that
    has since been overtaken by the code. Prefer rewriting a section to
    appending a paragraph to it.
- Not touch git, not commit, not close the issue. The driver does that.
- Report back in at most 15 lines: files changed, decisions made, and anything
  in the brief it deviated from or could not do.

**Never run two implementation subagents at once.** The issues are sequentially
dependent and they share `project.godot`, `.tscn` and `.csproj` files, which do
not merge. The parallelism is not worth the merge cost.

## 6. Per issue: verify, commit, close

**Verify it yourself.** A subagent reporting PASS is a claim; you produce the
evidence. The Godot binary is not on PATH — see `CLAUDE.md` and the toolchain
memory (on this machine,
`C:/Users/bornd/Downloads/godot/Godot_v4.7-stable_mono_win64_console.exe`, the
console build, which gives stdout):

```bash
dotnet build Arable.sln
<godot_console> --headless --path . res://scenes/dev/CameraSmokeTest.tscn
```

Run **every** smoke test in `scenes/dev/`, not only the one the issue named —
the older ones are the regression net that catches this issue breaking an
earlier one. Then read the diff (`git diff --stat`, then the changed files) and
check it against `Done when` yourself.

**Check the doc budget while you are in the diff** — a rule only the subagent is
asked to respect is decoration:

```bash
wc -l docs/implementation.md   # under ~700
awk '/^#{2,3} /{if(h!="")printf "%5d  %s\n", NR-s, h; h=$0; s=NR} \
     END{printf "%5d  %s\n", NR-s, h}' docs/implementation.md | sort -rn | head -5
```

If the file is over budget or a section is past ~120 lines, cut it back yourself
before committing, and say in the report what you cut. What comes out first is
inventory the code now states plainly — member lists, assertion narration,
anything a reader would go to the source for anyway. What never comes out is
rationale, rejected alternatives, deferrals and traps.

### Look at it, when the issue makes a visual claim

`Done when` boxes like "terrain variation is visible" or "the iso view reads at
farm zoom" cannot be settled by an assert. For those, render and look:

```bash
<godot_console> --path . res://scenes/dev/ScreenshotTest.tscn -- <scratchpad>/shots
```

**Not `--headless`** — that uses the dummy rasterizer, so there is no
framebuffer to read back and the run would hang. A real window opens for a few
seconds; that is the price. Then `Read` each PNG and say what you actually see.

Treat this as a smoke detector, not an assertion. It reliably catches gross
breakage — nothing rendered, pink missing-material, camera pitch wrong, geometry
at the wrong scale, UI off-screen. It does **not** license you to tick a
behavioural box on your own judgement: report what the frame shows, attach the
PNGs to the report with `SendUserFile`, and leave the aesthetic call to the
user.

When a screenshot surprises you, **check it against the code before calling it a
bug** — an empty-looking world may be exactly what the current code should draw.

Add a canonical view to `ScreenshotTest.cs` when a milestone introduces
something the existing views cannot show (a build-mode overlay, a HUD, crop
stages). Keep the views deterministic and settle by *time*, not frame count —
the camera rig smooths on `delta`, so a frame count settles differently on a
fast machine.

**Green** → one commit for this issue and nothing else:

```
<imperative subject, from the issue title>

Closes #<n>.
```

plus the `Co-Authored-By` / `Claude-Session` trailers from this session's
attribution instructions. Then close the issue with the SHA:

```bash
python scripts/gh_issues_publish.py close <n> --reason completed \
    --comment "Implemented in <sha> on branch milestone/M5. Build and smoke tests pass."
```

One commit per issue, always — so a bad issue reverts without unwinding the
milestone.

**Red** → exactly one repair pass. Fix it yourself if it is small and obvious (a
compile error, a wrong resource path); hand it to a *new* subagent with the
failure output if it is not. Still red after that,
`git restore . && git clean -fd` to discard this issue's work, leave the issue
open, comment on it with the failure, and halt.

## 7. Milestone acceptance

Only when every ready issue is closed: go back to the tracker and check its
`Done when` boxes **yourself**, against the built game, not against the issues
you just closed. Boxes like "the camera pans with WASD and rotates in 90° steps"
are behavioural — a passing smoke test is evidence, a closed issue is not.

Run `ScreenshotTest.tscn` once more here and look at every view, whatever the
individual issues claimed. A milestone can pass issue by issue and still look
broken as a whole.

- All boxes verified → tick them (`update <tracker> --body-file`), then close
  the tracker with a comment naming the branch and the commits.
- Any box unmet, or blocked issues remain → leave the tracker **open**, comment
  on it with exactly which boxes are unmet and why, and say so in the report.
  Never close a tracker on the strength of its children alone.

## 8. Halt conditions

Stop the run and report — without starting another issue — when any of these
hit:

- Two issues in a row failed verification.
- An issue's `Done when` cannot be checked by a build or a smoke test, and the
  user has not said to proceed on judgement.
- A subagent reports a deviation that changes the design. That is the user's
  call, not yours.
- The user says stop, or asks anything mid-run.

Halting is a normal outcome, not a failure. Finish the report cleanly.

## 9. Report

Close with a compact table — issue number, title, and one of `closed <sha>` /
`failed: <reason>` / `blocked: needs-review` / `skipped: <reason>` — then the
tracker's state, the branch name, and the next step (review the branch, merge
it, or review the blocked issues on GitHub).

No prose narration of the code that was written: the diffs and the closed issues
are the record.
