# Scripts

Entry points meant to be driven by a human at a prompt *or* by an agent/skill.
Stdlib only — Python 3.9+, no `pip install` step.

| Script | Direction | Subcommands |
| --- | --- | --- |
| `verify.py` | local + CI gate | `--build`, `--import`, `--tests`, `--test NAME`, `--docs` |
| `gh_issues_publish.py` | write | `create`, `batch`, `update`, `milestone`, `comment`, `close`, `reopen` |
| `gh_issues_read.py` | read | `list`, `get`, `comments`, `search`, `labels`, `milestones` |

`ghlib.py` holds the auth, repo resolution, HTTP and pagination the two GitHub
scripts share.

## verify.py — the green gate

```bash
python scripts/verify.py                      # build, import, every smoke test, doc budget
python scripts/verify.py --build              # just the build, for a tight edit loop
python scripts/verify.py --test SimSmokeTest  # one scene; repeatable
python scripts/verify.py --docs               # docs/implementation budget only
```

With no flags everything runs; any flag selects only what it names. Every
selected check runs even after one fails, so **one invocation reports the whole
picture** — that is the point, both for a human and for an agent paying a
round-trip per command. Output is a line per check on success and the failing
command's verbatim output on failure. Exit status is 0 only if all of it passed.

Three things it exists to keep in one place:

- **`$GODOT`** is how it finds the engine — the Godot 4 .NET **console** build,
  since on Windows the ordinary build writes nothing to stdout. CI sets it in
  the workflow; set it yourself locally.
- **Green is stricter than exit 0.** A scene whose resources failed to import
  still quits 0 while logging the failure, so any `ERROR`, `WARNING` or
  `SCRIPT ERROR` line the engine writes fails the run. A bare
  `godot ... | grep PASS` drops both halves of that check: the pipe replaces
  Godot's exit status with grep's, and the grep filters out the very lines that
  matter.
- **The smoke test list is discovered**, by globbing `scenes/dev/*SmokeTest.tscn`
  — never written down. Adding a scene wires it into local runs, the milestone
  skill and CI at once. `ScreenshotTest` is excluded by that pattern on purpose:
  it must run windowed, and `verify.py` is headless throughout.

`--timeout` (default 180s per command) is what kills a hung scene: a missing C#
assembly makes a test hang rather than fail, and the run needs to name which one
and still report the rest. `--no-import` skips the pre-test asset import, which
CI uses because it imports in its own step.

## Auth and repo

Both resolve credentials and target repo automatically, so most invocations need
neither flag:

- **Token:** `--token` → `$GITHUB_TOKEN` → `$GH_TOKEN` → `gh auth token`.
  Needs `repo` scope for private repos; `public_repo` is enough for public ones.
- **Repo:** `--repo owner/name` → `$GITHUB_REPOSITORY` → the `origin` remote of
  the working tree (both `https://` and `git@` remotes are understood).

## Usage

```bash
# read
python scripts/gh_issues_read.py list --state open --label bug --limit 20
python scripts/gh_issues_read.py get 12 --comments
python scripts/gh_issues_read.py search "gridmap in:title" --state open
python scripts/gh_issues_read.py labels            # valid label names before writing

# write
python scripts/gh_issues_publish.py create --title "Chunked GridMap streaming" \
    --body-file notes.md --label perf,world --milestone "Alpha"
python scripts/gh_issues_publish.py milestone --title "M1 — World you can look at" \
    --description-file docs/m1.md --due 2026-10-15
python scripts/gh_issues_publish.py comment 12 --body "Fixed in 9cbd441."
python scripts/gh_issues_publish.py update 12 --add-label blocked --remove-label ready
python scripts/gh_issues_publish.py close 12 --reason not_planned --comment "Superseded."

# many at once, from a JSON list on stdin
echo '[{"title":"A","body":"...","labels":["bug"]}]' \
  | python scripts/gh_issues_publish.py batch --file - --dedupe
```

Every subcommand takes `--repo`, `--token`, `--json` and `-v`; every write
subcommand also takes `--dry-run`.

## Notes for agent / skill use

- **Machine-readable output.** `--json` emits the full objects; `--fields` trims
  them, e.g. `--json --fields number,title,state,labels`. Without `--json` the
  output is compact one-line-per-issue text that is cheap to feed back to a model.
- **Preview before writing.** `--dry-run` prints the exact payload and writes
  nothing — a good default for the plan step of an agent loop.
- **Idempotence.** `--dedupe` skips creating an issue whose title already exists
  (`--dedupe-state open|closed|all`, default `open`). GitHub's list endpoint lags
  a few seconds behind a write, so an issue created seconds ago may not be
  visible to the next scan; within one `batch` run this is handled internally.
- **Pull requests are excluded** from `list` by default — GitHub's issues
  endpoints return PRs too. Pass `--include-prs` to keep them.
- **Empty results are success** (exit 0), not failure.
- **Exit codes:** `0` success, `1` API or usage failure with a one-line reason on
  stderr, `2` argparse error, `130` interrupt.
- **Batch input schema:**

  ```json
  [
    {
      "title": "required",
      "body": "optional markdown",
      "labels": ["bug"],
      "assignees": ["octocat"],
      "milestone": "title or number"
    }
  ]
  ```

  A `{"issues": [...]}` wrapper is accepted too, and `labels`/`assignees` may be
  comma-separated strings instead of lists.
- **Milestones** accept a title or a number in both scripts; titles are resolved
  to numbers automatically, searching open *and* closed milestones.
- **`milestone` upserts by title.** It creates the milestone, or edits the one
  already carrying that title — so re-running the same command is an update, not
  a duplicate, and a generated set of milestones can be regenerated safely. Pass
  `--dedupe` to leave an existing milestone untouched instead of updating it.
  `--due` takes `YYYY-MM-DD` (anchored at midday UTC, so the displayed date does
  not slip a day) or a full ISO 8601 timestamp. There is no delete subcommand —
  removing a milestone is a rare, destructive action, so do it on the web UI.
