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
  would get swept into some issue's commit. Check it again *after* the gate
  below: Godot's import writes a `.uid` beside any script that lacks one, and
  those are tracked files (45 of them). If some appear, a previous commit
  shipped a script without its `.uid` — commit them on their own, before issue
  one, rather than letting them ride along in its diff.
- **Toolchain is green.** `python scripts/verify.py` passes *before* you change
  anything — the whole gate, not just the build. A run must start from green or
  you cannot tell whose failure it is. If it fails on `GODOT is not set`, stop
  and ask the user for the path rather than guessing one: it must be the Godot 4
  .NET **console** build, or nothing reaches stdout.
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

One subagent per issue, `subagent_type: "general-purpose"`, **`model: "sonnet"`**.
It gets the issue number, not your conversation — the brief is self-contained by
construction, and the clean context is the point.

**Why Sonnet implements and you don't.** The implementer's work is mechanical
against a vetted brief, and it lands behind a hard objective gate: you re-run
`verify.py` yourself and read the diff against `Done when`. A bad output gets
caught, not shipped — which is exactly the situation where the cheaper, faster
model is the right trade. Judgement stays with you (the doc cuts, the
screenshots, milestone acceptance), and the repair pass below escalates back to
Opus, because a failure the first model could not avoid is not usually one it
can diagnose. Do not quietly promote implementers to Opus because an issue
*looks* hard: a brief that genuinely needs design judgement is one the user
should see, which is a halt condition, not a model choice.

Prompt it to:

- Read the brief: `python scripts/gh_issues_read.py get <n>`.
- **Read the `docs/implementation/` files covering the subsystems it is about
  to touch — those files, not the set.** They are how previous issues hand over
  what they decided, and the rationale in them is the part not recoverable from
  the source. `docs/implementation.md` is the index mapping subsystem to file.
- Implement it, following `CLAUDE.md` and the settled decisions in
  `docs/tech.md` rather than re-deciding them.
- Make `python scripts/verify.py` pass — build, import, every smoke test, doc
  budget, one command. Use `--build` or `--test <Name>` while iterating and the
  bare form before reporting back. Write a new smoke test if `Done when` calls
  for one; dropping the `.tscn` into `scenes/dev/` is all the wiring it needs.
- **Work in few, large tool calls.** Every call re-reads the agent's whole
  context, so call count is the dominant cost — far more than the size of any
  one result. Concretely: prefer rewriting a file with `Write` over a long run
  of small `Edit`s; read a file **once**, with a line range, instead of `cat`
  then `sed` over the same file; never `cat` a source file over ~400 lines —
  `grep` for the symbol and read around it; and batch independent shell commands
  into a single call.
- Update the `docs/implementation/` file(s) it touched, adding a new one (and
  an index row) only for a genuinely new subsystem. This is the **only** channel
  by which the next issue's agent learns what this one decided — treat it as required output,
  not bookkeeping. Two rules on *how*, because that file is read by every later
  agent and its size is a running cost:
  - **Write the durable half only.** Why the shape was chosen, what was
    rejected, what is deferred and to which milestone, and the traps that cost
    an afternoon. Not member lists, not what a test asserts, not a restatement
    of what the code plainly says — that is derivable, and it goes stale
    silently.
  - **Stay inside the budget: no file over ~150 lines, no section over ~120.**
    Adding a feature is not licence to append. If a file has outgrown that, cut
    it back in the same commit — usually by deleting inventory that has since
    been overtaken by the code — or split it and add the index row. Prefer
    rewriting a section to appending a paragraph to it. The budget is per file
    so that one subsystem's notes never have to be paid for out of another's.
- Not touch git, not commit, not close the issue. The driver does that.
- Report back in at most 15 lines: files changed, decisions made, and anything
  in the brief it deviated from or could not do.

**Never run two implementation subagents at once.** The issues are sequentially
dependent and they share `project.godot`, `.tscn` and `.csproj` files, which do
not merge. The parallelism is not worth the merge cost.

## 6. Per issue: verify, commit, close

**Verify it yourself.** A subagent reporting PASS is a claim; you produce the
evidence. One command does all of it:

```bash
python scripts/verify.py
```

That is the build, the asset import, **every** smoke test in `scenes/dev/` — not
only the one the issue named, since the older ones are the regression net that
catches this issue breaking an earlier one — and the `docs/implementation`
budget, in one round-trip, with every check run even after one fails. It is the
same gate CI runs, so green here is a green PR. Never substitute a hand-rolled
`godot ... | grep PASS`: the pipe throws away Godot's exit status and the grep
throws away the `ERROR`/`WARNING` lines that make a run red.

Then read the diff (`git diff --stat`, then the changed files) and check it
against `Done when` yourself. That half is judgement and stays with you.

If `verify.py` reports the doc budget over — a rule only the subagent is asked
to respect is decoration — cut it back yourself before committing, and say in
the report what you cut. What comes out first is inventory the code now states
plainly: member lists, assertion narration, anything a reader would go to the
source for anyway. What never comes out is rationale, rejected alternatives,
deferrals and traps.

### Look at it, when the issue makes a visual claim

`Done when` boxes like "terrain variation is visible" or "the iso view reads at
farm zoom" cannot be settled by an assert. For those, render and look:

```bash
"$GODOT" --path . res://scenes/dev/ScreenshotTest.tscn -- <scratchpad>/shots
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
compile error, a wrong resource path); hand it to a *new* subagent — this one on
**Opus**, with the failure output — if it is not. Diagnosis is the part that
needs the stronger model, and the repair agent starts cold, so paste it the
failing output rather than telling it to re-run and find out. Still red after that,
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
