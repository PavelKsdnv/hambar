#!/usr/bin/env python3
"""Publish issues to a GitHub repository.

Write side of the pair; `gh_issues_read.py` is the read side.

Examples
--------
    python scripts/gh_issues_publish.py create \
        --title "Chunked GridMap streaming" --body-file notes.md --label perf,world

    python scripts/gh_issues_publish.py create --title "Dupe guard" --dedupe
    python scripts/gh_issues_publish.py comment 12 --body "Fixed in 9cbd441."
    python scripts/gh_issues_publish.py update 12 --add-label blocked --state closed
    python scripts/gh_issues_publish.py close 12 --reason not_planned
    cat issues.json | python scripts/gh_issues_publish.py batch --file - --dry-run

    python scripts/gh_issues_publish.py milestone --title "M1 — World you can \
        look at" --description-file m1.md --due 2026-10-15

Batch input is a JSON list (or {"issues": [...]}) of objects shaped like:
    {"title": "...", "body": "...", "labels": ["bug"],
     "assignees": ["octocat"], "milestone": "M1"}

Agent notes
-----------
* Every subcommand takes --json for machine-readable output and --dry-run so a
  plan can be shown before anything is written.
* --dedupe skips creating an issue whose title already exists, which makes
  repeated agent runs idempotent. Caveat: GitHub's issue *list* endpoint lags
  a few seconds behind a write, so an issue created moments ago may not be
  visible to the dedupe scan yet. Within a single `batch` run this is handled
  (titles created during the run are remembered); across back-to-back runs,
  leave a short gap or dedupe on a stable title.
* Exit codes: 0 success, 1 API/usage failure reported on stderr, 2 argparse.
"""

from __future__ import annotations

import argparse
import json
import sys

import ghlib
from ghlib import GhError


# --------------------------------------------------------------------------- helpers


def issue_line(issue: dict, prefix: str = "") -> str:
    # API responses carry label objects; dry-run payloads carry plain strings.
    labels = ",".join(
        label["name"] if isinstance(label, dict) else str(label)
        for label in issue.get("labels") or []
    )
    tail = "  [{}]".format(labels) if labels else ""
    return "{}#{} {}{}\n  {}".format(
        prefix, issue["number"], issue.get("title", ""), tail, issue.get("html_url", "")
    )


def emit(args, payload, text: str) -> None:
    if args.as_json:
        ghlib.dump_json(payload)
    else:
        print(text)


def existing_titles(client: ghlib.Client, state: str) -> dict:
    """Map normalized title -> issue, for --dedupe."""
    found = {}
    for item in client.paginate(
        "/repos/{}/issues".format(client.repo), {"state": state}
    ):
        if ghlib.is_pull_request(item):
            continue
        found.setdefault(item["title"].strip().lower(), item)
    return found


def build_issue_payload(client, title, body, labels, assignees, milestone) -> dict:
    payload = {"title": title}
    if body is not None:
        payload["body"] = body
    if labels:
        payload["labels"] = labels
    if assignees:
        payload["assignees"] = assignees
    number = ghlib.resolve_milestone(client, milestone)
    if number is not None:
        payload["milestone"] = number
    return payload


def create_one(client: ghlib.Client, payload: dict, dry_run: bool) -> dict:
    if dry_run:
        return dict(payload, number=0, html_url="(dry-run)", _dry_run=True)
    return client.post("/repos/{}/issues".format(client.repo), payload)


def milestone_line(milestone: dict, prefix: str = "") -> str:
    return "{}#{} {} [{}]\n  {}".format(
        prefix,
        milestone.get("number", 0),
        milestone.get("title", ""),
        milestone.get("state", "open"),
        milestone.get("html_url", ""),
    )


def normalize_due(value: str) -> str:
    """Accept a plain YYYY-MM-DD; the API wants an ISO 8601 timestamp.

    Anchored at midday UTC so the date GitHub displays does not slip a day for
    viewers either side of the meridian.
    """
    value = value.strip()
    if len(value) == 10 and value.count("-") == 2:
        return value + "T12:00:00Z"
    return value


# --------------------------------------------------------------------------- commands


def cmd_create(args) -> int:
    client = ghlib.make_client(args)
    body = ghlib.read_body(args.body, args.body_file)
    payload = build_issue_payload(
        client,
        args.title,
        body,
        ghlib.split_list(args.label),
        ghlib.split_list(args.assignee),
        args.milestone,
    )

    if args.dedupe:
        match = existing_titles(client, args.dedupe_state).get(args.title.strip().lower())
        if match:
            emit(args, match, issue_line(match, prefix="skipped (duplicate) "))
            return 0

    issue = create_one(client, payload, args.dry_run)
    emit(args, issue, issue_line(issue, prefix="dry-run " if args.dry_run else "created "))
    return 0


def cmd_batch(args) -> int:
    client = ghlib.make_client(args)
    raw = sys.stdin.read() if args.file == "-" else open(args.file, encoding="utf-8").read()
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise GhError("batch input is not valid JSON: {}".format(exc)) from exc
    if isinstance(data, dict):
        data = data.get("issues", [])
    if not isinstance(data, list):
        raise GhError('batch input must be a JSON list, or {"issues": [...]}')

    seen = existing_titles(client, args.dedupe_state) if args.dedupe else {}
    results = []
    for index, entry in enumerate(data):
        if not isinstance(entry, dict) or not entry.get("title"):
            raise GhError("batch entry {} needs a non-empty `title`".format(index))
        title = entry["title"]
        key = title.strip().lower()
        if args.dedupe and key in seen:
            match = seen[key]
            results.append(match)
            if not args.as_json:
                print(issue_line(match, prefix="skipped (duplicate) "))
            continue

        payload = build_issue_payload(
            client,
            title,
            entry.get("body"),
            ghlib.split_list(entry.get("labels")),
            ghlib.split_list(entry.get("assignees") or entry.get("assignee")),
            entry.get("milestone"),
        )
        issue = create_one(client, payload, args.dry_run)
        results.append(issue)
        if args.dedupe:
            seen[key] = issue
        if not args.as_json:
            print(issue_line(issue, prefix="dry-run " if args.dry_run else "created "))

    if args.as_json:
        ghlib.dump_json(results)
    return 0


def cmd_milestone(args) -> int:
    """Upsert a milestone by title: create it, or edit the one already there.

    Titles are the stable handle (`--milestone "M1 — ..."` elsewhere resolves by
    title), so re-running the same command is a no-op-shaped update rather than
    a duplicate. Pass --dedupe to leave an existing milestone untouched instead.
    """
    client = ghlib.make_client(args)
    description = ghlib.read_body(args.description, args.description_file)

    payload = {"title": args.title}
    if description is not None:
        payload["description"] = description
    if args.due is not None:
        payload["due_on"] = normalize_due(args.due)
    if args.state:
        payload["state"] = args.state

    existing = ghlib.find_milestone(client, args.title)
    if existing and args.dedupe:
        emit(args, existing, milestone_line(existing, prefix="skipped (duplicate) "))
        return 0

    path = "/repos/{}/milestones".format(client.repo)
    if args.dry_run:
        emit(
            args,
            dict(payload, number=existing["number"] if existing else 0,
                 html_url="(dry-run)", _dry_run=True),
            "dry-run milestone {}: {}".format(
                "update #{}".format(existing["number"]) if existing else "create",
                json.dumps(payload, ensure_ascii=False),
            ),
        )
        return 0

    if existing:
        milestone = client.patch("{}/{}".format(path, existing["number"]), payload)
        emit(args, milestone, milestone_line(milestone, prefix="updated "))
    else:
        milestone = client.post(path, payload)
        emit(args, milestone, milestone_line(milestone, prefix="created "))
    return 0


def cmd_update(args) -> int:
    client = ghlib.make_client(args)
    path = "/repos/{}/issues/{}".format(client.repo, args.number)
    payload = {}

    if args.title:
        payload["title"] = args.title
    body = ghlib.read_body(args.body, args.body_file)
    if body is not None:
        payload["body"] = body
    if args.state:
        payload["state"] = args.state
    if args.assignee:
        payload["assignees"] = ghlib.split_list(args.assignee)
    if args.milestone is not None:
        payload["milestone"] = (
            None if args.milestone == "" else ghlib.resolve_milestone(client, args.milestone)
        )

    add = ghlib.split_list(args.add_label)
    remove = ghlib.split_list(args.remove_label)
    if args.label:
        payload["labels"] = ghlib.split_list(args.label)
    elif add or remove:
        current = [label["name"] for label in client.get(path).get("labels") or []]
        drop = {name.lower() for name in remove}
        merged = [name for name in current if name.lower() not in drop]
        for name in add:
            if name.lower() not in {m.lower() for m in merged}:
                merged.append(name)
        payload["labels"] = merged

    if not payload:
        raise GhError("nothing to update; pass --title/--body/--label/--state/...")

    if args.dry_run:
        emit(args, dict(payload, number=args.number, _dry_run=True),
             "dry-run update #{}: {}".format(args.number, json.dumps(payload)))
        return 0

    issue = client.patch(path, payload)
    emit(args, issue, issue_line(issue, prefix="updated "))
    return 0


def cmd_comment(args) -> int:
    client = ghlib.make_client(args)
    body = ghlib.read_body(args.body, args.body_file)
    if body is None:
        body = sys.stdin.read()
    if not body.strip():
        raise GhError("comment body is empty")

    if args.dry_run:
        emit(args, {"issue": args.number, "body": body, "_dry_run": True},
             "dry-run comment on #{}:\n{}".format(args.number, body))
        return 0

    comment = client.post(
        "/repos/{}/issues/{}/comments".format(client.repo, args.number), {"body": body}
    )
    emit(args, comment, "commented on #{}\n  {}".format(args.number, comment["html_url"]))
    return 0


def cmd_close(args) -> int:
    client = ghlib.make_client(args)
    if args.comment and not args.dry_run:
        client.post(
            "/repos/{}/issues/{}/comments".format(client.repo, args.number),
            {"body": args.comment},
        )

    payload = {"state": "closed", "state_reason": args.reason}
    if args.dry_run:
        emit(args, dict(payload, number=args.number, _dry_run=True),
             "dry-run close #{} ({})".format(args.number, args.reason))
        return 0
    issue = client.patch(
        "/repos/{}/issues/{}".format(client.repo, args.number), payload
    )
    emit(args, issue, issue_line(issue, prefix="closed "))
    return 0


def cmd_reopen(args) -> int:
    client = ghlib.make_client(args)
    payload = {"state": "open", "state_reason": "reopened"}
    if args.dry_run:
        emit(args, dict(payload, number=args.number, _dry_run=True),
             "dry-run reopen #{}".format(args.number))
        return 0
    issue = client.patch(
        "/repos/{}/issues/{}".format(client.repo, args.number), payload
    )
    emit(args, issue, issue_line(issue, prefix="reopened "))
    return 0


# --------------------------------------------------------------------------- cli


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="gh_issues_publish.py",
        description="Publish issues, comments and state changes to GitHub.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    subs = parser.add_subparsers(dest="command", required=True)

    def add_body_args(sub, help_suffix="issue body"):
        sub.add_argument("--body", help=help_suffix)
        sub.add_argument("--body-file", help="read the body from a file, or '-' for stdin")

    def add_shared(sub):
        ghlib.add_common_args(sub)
        sub.add_argument(
            "--dry-run", action="store_true", help="show what would be sent, write nothing"
        )

    create = subs.add_parser("create", help="create a single issue")
    create.add_argument("--title", required=True)
    add_body_args(create)
    create.add_argument("--label", action="append", help="repeatable, or comma-separated")
    create.add_argument("--assignee", action="append", help="repeatable, or comma-separated")
    create.add_argument("--milestone", help="milestone number or title")
    create.add_argument(
        "--dedupe", action="store_true", help="skip if an issue with this title exists"
    )
    create.add_argument(
        "--dedupe-state", choices=("open", "closed", "all"), default="open"
    )
    add_shared(create)
    create.set_defaults(func=cmd_create)

    batch = subs.add_parser("batch", help="create many issues from a JSON file")
    batch.add_argument("--file", default="-", help="JSON file path, or '-' for stdin")
    batch.add_argument("--dedupe", action="store_true", help="skip titles that already exist")
    batch.add_argument(
        "--dedupe-state", choices=("open", "closed", "all"), default="open"
    )
    add_shared(batch)
    batch.set_defaults(func=cmd_batch)

    milestone = subs.add_parser(
        "milestone", help="create a milestone, or update the one with that title"
    )
    milestone.add_argument("--title", required=True)
    milestone.add_argument("--description")
    milestone.add_argument(
        "--description-file", help="read the description from a file, or '-' for stdin"
    )
    milestone.add_argument("--due", help="YYYY-MM-DD, or a full ISO 8601 timestamp")
    milestone.add_argument("--state", choices=("open", "closed"))
    milestone.add_argument(
        "--dedupe",
        action="store_true",
        help="leave an existing milestone with this title untouched",
    )
    add_shared(milestone)
    milestone.set_defaults(func=cmd_milestone)

    update = subs.add_parser("update", help="edit an existing issue")
    update.add_argument("number", type=int)
    update.add_argument("--title")
    add_body_args(update, "replacement issue body")
    update.add_argument("--label", action="append", help="replace all labels")
    update.add_argument("--add-label", action="append", help="add to existing labels")
    update.add_argument("--remove-label", action="append", help="remove from existing labels")
    update.add_argument("--assignee", action="append", help="replace all assignees")
    update.add_argument("--milestone", help="number or title; empty string clears it")
    update.add_argument("--state", choices=("open", "closed"))
    add_shared(update)
    update.set_defaults(func=cmd_update)

    comment = subs.add_parser("comment", help="add a comment to an issue")
    comment.add_argument("number", type=int)
    add_body_args(comment, "comment text (default: stdin)")
    add_shared(comment)
    comment.set_defaults(func=cmd_comment)

    close = subs.add_parser("close", help="close an issue")
    close.add_argument("number", type=int)
    close.add_argument("--reason", choices=("completed", "not_planned"), default="completed")
    close.add_argument("--comment", help="post this comment before closing")
    add_shared(close)
    close.set_defaults(func=cmd_close)

    reopen = subs.add_parser("reopen", help="reopen a closed issue")
    reopen.add_argument("number", type=int)
    add_shared(reopen)
    reopen.set_defaults(func=cmd_reopen)

    return parser


def main() -> int:
    args = build_parser().parse_args()
    return args.func(args)


if __name__ == "__main__":
    ghlib.run(main)
