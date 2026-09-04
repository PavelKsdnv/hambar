"""Shared GitHub helpers for the issue scripts.

Kept dependency-free (stdlib only) so the scripts run anywhere Python 3.9+ is
available, including inside agent/skill sandboxes with no pip install step.

Auth resolution order:
    1. --token argument
    2. $GITHUB_TOKEN
    3. $GH_TOKEN
    4. `gh auth token` (the GitHub CLI's stored credential)

Repo resolution order:
    1. --repo owner/name
    2. $GITHUB_REPOSITORY (set automatically inside GitHub Actions)
    3. the `origin` git remote of the current working tree
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Iterator, Optional

API_ROOT = "https://api.github.com"
USER_AGENT = "arable-issue-scripts"
TIMEOUT = 30


class GhError(Exception):
    """Any failure worth reporting to the caller with a clean message."""


def eprint(*args: object) -> None:
    print(*args, file=sys.stderr)


# --------------------------------------------------------------------------- auth


def resolve_token(explicit: Optional[str] = None) -> str:
    if explicit:
        return explicit.strip()
    for var in ("GITHUB_TOKEN", "GH_TOKEN"):
        value = os.environ.get(var)
        if value and value.strip():
            return value.strip()
    gh = shutil.which("gh")
    if gh:
        try:
            out = subprocess.run(
                [gh, "auth", "token"], capture_output=True, text=True, timeout=15
            )
        except (OSError, subprocess.SubprocessError) as exc:
            raise GhError("could not run `gh auth token`: {}".format(exc)) from exc
        if out.returncode == 0 and out.stdout.strip():
            return out.stdout.strip()
    raise GhError(
        "no GitHub token found. Set $GITHUB_TOKEN, or run `gh auth login` so "
        "`gh auth token` can supply one."
    )


# --------------------------------------------------------------------------- repo


_REMOTE_RE = re.compile(
    r"""(?:git@|ssh://git@|https://|http://)   # scheme / ssh prefix
        [^/:]+[:/]                             # host separator
        (?P<owner>[^/]+)/(?P<name>[^/]+?)      # owner/name
        (?:\.git)?/?$""",
    re.VERBOSE,
)


def resolve_repo(explicit: Optional[str] = None) -> str:
    if explicit:
        return _validate_repo(explicit)
    env = os.environ.get("GITHUB_REPOSITORY")
    if env:
        return _validate_repo(env)
    git = shutil.which("git")
    if git:
        out = subprocess.run(
            ["git", "remote", "get-url", "origin"], capture_output=True, text=True
        )
        if out.returncode == 0:
            match = _REMOTE_RE.match(out.stdout.strip())
            if match:
                return "{}/{}".format(match.group("owner"), match.group("name"))
    raise GhError(
        "could not determine the repository. Pass --repo owner/name or run "
        "from a checkout with an `origin` remote."
    )


def _validate_repo(value: str) -> str:
    value = value.strip()
    if value.endswith(".git"):
        value = value[: -len(".git")]
    if value.count("/") != 1 or not all(value.split("/")):
        raise GhError("--repo must look like owner/name, got {!r}".format(value))
    return value


# --------------------------------------------------------------------------- http


class Client:
    def __init__(self, repo: str, token: str, verbose: bool = False):
        self.repo = repo
        self.token = token
        self.verbose = verbose

    def request(self, method: str, path: str, data: Any = None, params: Any = None):
        """Return (parsed_json, headers). `path` is relative to the API root."""
        url = path if path.startswith("http") else API_ROOT + path
        if params:
            clean = {k: v for k, v in params.items() if v is not None}
            if clean:
                url += ("&" if "?" in url else "?") + urllib.parse.urlencode(clean)
        payload = json.dumps(data).encode("utf-8") if data is not None else None

        req = urllib.request.Request(url, data=payload, method=method)
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("X-GitHub-Api-Version", "2022-11-28")
        req.add_header("Authorization", "Bearer " + self.token)
        req.add_header("User-Agent", USER_AGENT)
        if payload is not None:
            req.add_header("Content-Type", "application/json")

        if self.verbose:
            eprint("-> {} {}".format(method, url))
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                raw = resp.read()
                body = json.loads(raw) if raw else None
                return body, dict(resp.headers)
        except urllib.error.HTTPError as exc:
            raise GhError(self._http_message(method, url, exc)) from exc
        except urllib.error.URLError as exc:
            raise GhError("network error calling {}: {}".format(url, exc.reason)) from exc

    def _http_message(self, method: str, url: str, exc: urllib.error.HTTPError) -> str:
        try:
            detail = json.loads(exc.read())
        except Exception:
            detail = None
        parts = ["{} {} failed: HTTP {}".format(method, url, exc.code)]
        if isinstance(detail, dict):
            if detail.get("message"):
                parts.append(str(detail["message"]))
            for err in detail.get("errors") or []:
                if isinstance(err, dict):
                    parts.append(
                        " ".join(
                            str(err[k])
                            for k in ("resource", "field", "code", "message")
                            if err.get(k)
                        )
                    )
                else:
                    parts.append(str(err))
        if exc.code == 403 and exc.headers.get("x-ratelimit-remaining") == "0":
            parts.append("rate limit exhausted; retry after the reset window")
        if exc.code == 404:
            parts.append(
                "check the issue number, that {} exists, and that the token "
                "carries `repo` scope".format(self.repo)
            )
        return " - ".join(parts)

    def get(self, path: str, params: Any = None):
        return self.request("GET", path, params=params)[0]

    def post(self, path: str, data: Any):
        return self.request("POST", path, data=data)[0]

    def patch(self, path: str, data: Any):
        return self.request("PATCH", path, data=data)[0]

    def paginate(
        self, path: str, params: Any = None, limit: Optional[int] = None
    ) -> Iterator[dict]:
        """Yield items across pages, stopping once `limit` items are produced."""
        params = dict(params or {})
        params.setdefault("per_page", 100)
        url = path
        produced = 0
        while url:
            body, headers = self.request("GET", url, params=params)
            params = None  # already encoded into the `next` link
            items = body.get("items", body) if isinstance(body, dict) else body
            for item in items or []:
                yield item
                produced += 1
                if limit is not None and produced >= limit:
                    return
            url = _next_link(headers.get("Link"))


def _next_link(link_header: Optional[str]) -> Optional[str]:
    if not link_header:
        return None
    for chunk in link_header.split(","):
        part = chunk.strip()
        if part.endswith('rel="next"') and part.startswith("<"):
            return part[1 : part.index(">")]
    return None


# --------------------------------------------------------------------------- misc


def read_body(text: Optional[str], path: Optional[str]) -> Optional[str]:
    """Resolve an issue/comment body from --body, --body-file, or stdin ('-')."""
    if text is not None and path is not None:
        raise GhError("use either --body or --body-file, not both")
    if text is not None:
        return text
    if path is None:
        return None
    if path == "-":
        return sys.stdin.read()
    try:
        with open(path, encoding="utf-8") as handle:
            return handle.read()
    except OSError as exc:
        raise GhError("could not read body file {}: {}".format(path, exc)) from exc


def split_list(values) -> list:
    """Flatten repeated flags and comma-separated values into one clean list.

    Accepts a list of strings (repeated CLI flags) or a single string, so JSON
    batch entries may write "labels": "a,b" as well as "labels": ["a", "b"].
    """
    if isinstance(values, str):
        values = [values]
    out = []
    for value in values or []:
        out.extend(part.strip() for part in str(value).split(",") if part.strip())
    return out


def resolve_milestone(client: "Client", value: Optional[str]) -> Optional[int]:
    """Accept a milestone number or title; the API only takes a number."""
    if value is None:
        return None
    if str(value).isdigit():
        return int(value)
    for milestone in client.paginate(
        "/repos/{}/milestones".format(client.repo), {"state": "all"}
    ):
        if milestone["title"].strip().lower() == str(value).strip().lower():
            return milestone["number"]
    raise GhError("no milestone titled {!r} in {}".format(value, client.repo))


def is_pull_request(item: dict) -> bool:
    """The issues endpoints return PRs too; callers almost never want them."""
    return "pull_request" in item


def dump_json(value: Any) -> None:
    json.dump(value, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write("\n")


def add_common_args(parser) -> None:
    parser.add_argument(
        "--repo", help="owner/name (default: $GITHUB_REPOSITORY or the origin remote)"
    )
    parser.add_argument(
        "--token", help="GitHub token (default: $GITHUB_TOKEN, $GH_TOKEN, gh auth token)"
    )
    parser.add_argument(
        "--json", action="store_true", dest="as_json", help="emit raw JSON on stdout"
    )
    parser.add_argument(
        "-v", "--verbose", action="store_true", help="log API requests to stderr"
    )


def make_client(args) -> "Client":
    return Client(
        repo=resolve_repo(getattr(args, "repo", None)),
        token=resolve_token(getattr(args, "token", None)),
        verbose=getattr(args, "verbose", False),
    )


def run(main_fn) -> None:
    """Shared entry point: map GhError to exit 1 with a clean stderr message."""
    try:
        sys.exit(main_fn() or 0)
    except GhError as exc:
        eprint("error: {}".format(exc))
        sys.exit(1)
    except KeyboardInterrupt:
        sys.exit(130)
    except BrokenPipeError:
        sys.exit(0)
