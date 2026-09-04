#!/usr/bin/env python3
"""Read issues from a GitHub repository.

Read side of the pair; `gh_issues_publish.py` is the write side.

Examples
--------
    python scripts/gh_issues_read.py list --state open --label bug --limit 20
    python scripts/gh_issues_read.py get 12 --comments
    python scripts/gh_issues_read.py search "gridmap in:title" --limit 5
    python scripts/gh_issues_read.py comments 12 --json
    python scripts/gh_issues_read.py labels

Agent notes
-----------
* Default output is compact plain text, cheap to feed back into a model.
  --json gives the full API objects; --fields trims JSON to named keys, e.g.
  `--json --fields number,title,state,labels`.
* Pull requests are filtered out of issue listings by default (GitHub's issues
  endpoints include them); pass --include-prs to keep them.
* An empty result is success (exit 0), not an error.
* Exit codes: 0 success, 1 API/usage failure reported on stderr, 2 argparse.
"""

from __future__ import annotations

import argparse

import ghlib
from ghlib import GhError

SUMMARY_FIELDS = (
    "number",
    "title",
    "state",
    "labels",
    "assignees",
    "milestone",
    "comments",
    "created_at",
    "updated_at",
    "html_url",
)


# --------------------------------------------------------------------------- shaping


def names(items, key: str = "name") -> list:
    return [item[key] for item in items or [] if isinstance(item, dict)]


def summarize(issue: dict) -> dict:
    """The subset of an issue worth handing to a model or a shell pipeline."""
    milestone = issue.get("milestone") or {}
    return {
        "number": issue.get("number"),
        "title": issue.get("title"),
        "state": issue.get("state"),
        "state_reason": issue.get("state_reason"),
        "labels": names(issue.get("labels")),
        "assignees": names(issue.get("assignees"), "login"),
        "milestone": milestone.get("title"),
        "author": (issue.get("user") or {}).get("login"),
        "comments": issue.get("comments"),
        "created_at": issue.get("created_at"),
        "updated_at": issue.get("updated_at"),
        "html_url": issue.get("html_url"),
        "body": issue.get("body"),
    }


def pick(data, fields):
    if not fields:
        return data
    if isinstance(data, list):
        return [pick(item, fields) for item in data]
    return {key: data.get(key) for key in fields}


def one_line(issue: dict) -> str:
    view = summarize(issue)
    bits = ["#{}".format(view["number"]), "[{}]".format(view["state"]), view["title"] or ""]
    if view["labels"]:
        bits.append("({})".format(",".join(view["labels"])))
    if view["assignees"]:
        bits.append("@" + ",@".join(view["assignees"]))
    if view["comments"]:
        bits.append("{}c".format(view["comments"]))
    return " ".join(bits)


def detail(issue: dict) -> str:
    view = summarize(issue)
    lines = [
        "#{} {}".format(view["number"], view["title"]),
        "state:     {}{}".format(
            view["state"],
            " ({})".format(view["state_reason"]) if view["state_reason"] else "",
        ),
        "author:    {}".format(view["author"]),
        "labels:    {}".format(", ".join(view["labels"]) or "-"),
        "assignees: {}".format(", ".join(view["assignees"]) or "-"),
        "milestone: {}".format(view["milestone"] or "-"),
        "created:   {}   updated: {}".format(view["created_at"], view["updated_at"]),
        "url:       {}".format(view["html_url"]),
        "",
        (view["body"] or "(no body)").strip(),
    ]
    return "\n".join(lines)


def comment_block(comment: dict) -> str:
    return "--- {} at {}\n{}".format(
        (comment.get("user") or {}).get("login"),
        comment.get("created_at"),
        (comment.get("body") or "").strip(),
    )


def output(args, payload, text: str) -> None:
    if args.as_json:
        ghlib.dump_json(pick(payload, ghlib.split_list(args.fields)))
    else:
        print(text)


def add_output_args(sub) -> None:
    ghlib.add_common_args(sub)
    sub.add_argument(
        "--fields",
        action="append",
        help="with --json, keep only these keys (comma-separated); "
        "available: " + ",".join(SUMMARY_FIELDS) + ",body,author,state_reason",
    )


# --------------------------------------------------------------------------- commands


def cmd_list(args) -> int:
    client = ghlib.make_client(args)
    params = {
        "state": args.state,
        "labels": ",".join(ghlib.split_list(args.label)) or None,
        "assignee": args.assignee,
        "creator": args.creator,
        "milestone": args.milestone,
        "since": args.since,
        "sort": args.sort,
        "direction": args.direction,
    }
    # Ask for extra rows because PRs share this endpoint and get filtered below.
    fetch_limit = None if args.limit is None else max(args.limit * 2, args.limit + 10)

    issues = []
    for item in client.paginate(
        "/repos/{}/issues".format(client.repo), params, limit=fetch_limit
    ):
        if not args.include_prs and ghlib.is_pull_request(item):
            continue
        issues.append(item)
        if args.limit is not None and len(issues) >= args.limit:
            break

    output(
        args,
        [summarize(issue) for issue in issues],
        "\n".join(one_line(issue) for issue in issues),
    )
    return 0


def cmd_get(args) -> int:
    client = ghlib.make_client(args)
    issue = client.get("/repos/{}/issues/{}".format(client.repo, args.number))
    payload = summarize(issue)
    text = detail(issue)

    if args.comments:
        comments = list(
            client.paginate(
                "/repos/{}/issues/{}/comments".format(client.repo, args.number)
            )
        )
        payload["comment_thread"] = [
            {
                "author": (c.get("user") or {}).get("login"),
                "created_at": c.get("created_at"),
                "body": c.get("body"),
            }
            for c in comments
        ]
        if comments:
            text += "\n\n" + "\n\n".join(comment_block(c) for c in comments)

    output(args, payload, text)
    return 0


def cmd_comments(args) -> int:
    client = ghlib.make_client(args)
    comments = list(
        client.paginate("/repos/{}/issues/{}/comments".format(client.repo, args.number))
    )
    output(
        args,
        [
            {
                "id": c.get("id"),
                "author": (c.get("user") or {}).get("login"),
                "created_at": c.get("created_at"),
                "body": c.get("body"),
                "html_url": c.get("html_url"),
            }
            for c in comments
        ],
        "\n\n".join(comment_block(c) for c in comments),
    )
    return 0


def cmd_search(args) -> int:
    client = ghlib.make_client(args)
    query = "repo:{} is:issue {}".format(client.repo, args.query).strip()
    if args.state:
        query += " is:{}".format(args.state)
    issues = list(
        client.paginate(
            "/search/issues", {"q": query, "sort": args.sort, "order": args.direction},
            limit=args.limit,
        )
    )
    output(
        args,
        [summarize(issue) for issue in issues],
        "\n".join(one_line(issue) for issue in issues),
    )
    return 0


def cmd_labels(args) -> int:
    client = ghlib.make_client(args)
    labels = list(client.paginate("/repos/{}/labels".format(client.repo)))
    output(
        args,
        [{"name": l["name"], "color": l["color"], "description": l.get("description")}
         for l in labels],
        "\n".join(
            "{}  {}".format(l["name"], l.get("description") or "").rstrip()
            for l in labels
        ),
    )
    return 0


def cmd_milestones(args) -> int:
    client = ghlib.make_client(args)
    milestones = list(
        client.paginate("/repos/{}/milestones".format(client.repo), {"state": args.state})
    )
    output(
        args,
        [
            {
                "number": m["number"],
                "title": m["title"],
                "state": m["state"],
                "open_issues": m.get("open_issues"),
                "closed_issues": m.get("closed_issues"),
                "due_on": m.get("due_on"),
            }
            for m in milestones
        ],
        "\n".join(
            "#{} {} [{}] {} open / {} closed".format(
                m["number"], m["title"], m["state"],
                m.get("open_issues", 0), m.get("closed_issues", 0)
            )
            for m in milestones
        ),
    )
    return 0


# --------------------------------------------------------------------------- cli


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="gh_issues_read.py",
        description="Read issues, comments, labels and milestones from GitHub.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    subs = parser.add_subparsers(dest="command", required=True)

    listing = subs.add_parser("list", help="list issues")
    listing.add_argument("--state", choices=("open", "closed", "all"), default="open")
    listing.add_argument("--label", action="append", help="repeatable; ANDed by GitHub")
    listing.add_argument("--assignee", help="login, 'none', or '*' for any")
    listing.add_argument("--creator", help="login of the issue author")
    listing.add_argument("--milestone", help="milestone number, '*' or 'none'")
    listing.add_argument("--since", help="ISO 8601 timestamp; only issues updated after")
    listing.add_argument(
        "--sort", choices=("created", "updated", "comments"), default="created"
    )
    listing.add_argument("--direction", choices=("asc", "desc"), default="desc")
    listing.add_argument("--limit", type=int, default=30, help="0 or less means no limit")
    listing.add_argument(
        "--include-prs", action="store_true", help="keep pull requests in the results"
    )
    add_output_args(listing)
    listing.set_defaults(func=cmd_list)

    get = subs.add_parser("get", help="show one issue in full")
    get.add_argument("number", type=int)
    get.add_argument("--comments", action="store_true", help="include the comment thread")
    add_output_args(get)
    get.set_defaults(func=cmd_get)

    comments = subs.add_parser("comments", help="show an issue's comments")
    comments.add_argument("number", type=int)
    add_output_args(comments)
    comments.set_defaults(func=cmd_comments)

    search = subs.add_parser("search", help="search issues with GitHub query syntax")
    search.add_argument("query", help='e.g. "gridmap in:title label:perf"')
    search.add_argument("--state", choices=("open", "closed"))
    search.add_argument(
        "--sort", choices=("created", "updated", "comments", "reactions"), default="updated"
    )
    search.add_argument("--direction", choices=("asc", "desc"), default="desc")
    search.add_argument("--limit", type=int, default=30, help="0 or less means no limit")
    add_output_args(search)
    search.set_defaults(func=cmd_search)

    labels = subs.add_parser("labels", help="list the repo's labels")
    add_output_args(labels)
    labels.set_defaults(func=cmd_labels)

    milestones = subs.add_parser("milestones", help="list the repo's milestones")
    milestones.add_argument("--state", choices=("open", "closed", "all"), default="open")
    add_output_args(milestones)
    milestones.set_defaults(func=cmd_milestones)

    return parser


def main() -> int:
    args = build_parser().parse_args()
    if getattr(args, "limit", None) is not None and args.limit <= 0:
        args.limit = None
    return args.func(args)


if __name__ == "__main__":
    ghlib.run(main)
