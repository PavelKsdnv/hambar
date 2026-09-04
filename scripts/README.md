# GitHub issue scripts

Two entry points around the GitHub REST API, meant to be driven by a human at a
prompt *or* by an agent/skill:

| Script | Direction | Subcommands |
| --- | --- | --- |
| `gh_issues_publish.py` | write | `create`, `batch`, `update`, `comment`, `close`, `reopen` |
| `gh_issues_read.py` | read | `list`, `get`, `comments`, `search`, `labels`, `milestones` |

`ghlib.py` holds the auth, repo resolution, HTTP and pagination they share.
Stdlib only — Python 3.9+, no `pip install` step.

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
  to numbers automatically.
