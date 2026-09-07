#!/usr/bin/env python3
"""Run the project's green gate: build, asset import, every smoke test, doc budget.

One command, one verdict, so the definition of "green" lives in exactly one
place -- this file -- instead of being restated in CLAUDE.md prose, the
milestone skill and .github/workflows/ci.yml, where it has drifted before.

Two rules it exists to enforce, both of which an ad-hoc `godot ... | grep PASS`
silently drops:

  * A Godot run that exits 0 is not necessarily green. A scene whose resources
    failed to import still quits 0 while logging the failure, so any
    ERROR/WARNING/SCRIPT ERROR line the engine writes is fatal here -- the same
    rule .github/scripts/run_godot.sh applies in CI.
  * The smoke test list is discovered from scenes/dev/*SmokeTest.tscn, never
    written down. Adding a test wires it into local runs and CI at once.

Every selected check runs even after one fails, so a single invocation reports
the whole picture rather than the first problem.

The Godot binary comes from $GODOT -- on Windows that must be the .NET *console*
build, or the engine writes nothing to stdout.

Exit status is 0 only if every selected check passed.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOLUTION = "Arable.sln"
DEV_SCENES = ROOT / "scenes" / "dev"
DOCS_INDEX = ROOT / "docs" / "implementation.md"
DOCS_DIR = ROOT / "docs" / "implementation"

# docs/implementation budget, per CLAUDE.md. A file over these gets cut back in
# the same commit that pushed it over, not appended to.
FILE_BUDGET = 150
SECTION_BUDGET = 120

DEFAULT_TIMEOUT = 180

ANSI = re.compile(r"\x1b\[[0-9;]*m")
COMPLAINT = re.compile(r"^(ERROR|WARNING|SCRIPT ERROR):")
VERDICT = re.compile(r"\b(PASS|FAIL)\b")
HEADING = re.compile(r"^#{2,3} ")


def godot_binary() -> str:
    """The Godot 4 .NET console build, from $GODOT or the PATH."""
    exe = os.environ.get("GODOT")
    if exe:
        return exe
    found = shutil.which("godot") or shutil.which("godot.exe")
    if found:
        return found
    sys.exit(
        "GODOT is not set and no 'godot' is on the PATH.\n"
        "Point it at the Godot 4 .NET *console* build, e.g.\n"
        "  export GODOT=/c/path/to/Godot_v4.7-stable_mono_win64_console.exe"
    )


def run(cmd, timeout):
    """Run from the repo root, capture combined output, never raise on failure.

    A None status means the command could not be run or did not finish -- that
    is a failure too, and the text says which.
    """
    try:
        done = subprocess.run(
            cmd,
            cwd=str(ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout,
        )
    except FileNotFoundError as err:
        return None, "could not run {0!r}: {1}".format(cmd[0], err)
    except subprocess.TimeoutExpired as err:
        partial = (err.output or b"").decode("utf-8", "replace")
        return None, "{0}\n[no exit after {1}s -- killed]".format(partial, timeout)
    return done.returncode, done.stdout.decode("utf-8", "replace")


def godot_complaint(status, output):
    """Why this Godot run is not green, or None if it is."""
    if status is None:
        lines = output.strip().splitlines()
        return lines[-1] if lines else "did not run"
    if status != 0:
        return "exited with status {0}".format(status)
    logged = [ln for ln in ANSI.sub("", output).splitlines() if COMPLAINT.match(ln)]
    if logged:
        return "exited 0 but the engine logged:\n" + "\n".join(logged)
    return None


def smoke_tests():
    """Every smoke test scene, discovered rather than listed.

    ScreenshotTest is deliberately not one: it must run windowed, since under
    --headless the rasterizer is a dummy and there is no framebuffer to read.
    """
    return sorted(p.stem for p in DEV_SCENES.glob("*SmokeTest.tscn"))


def doc_budget():
    """Over-budget files and sections, plus a one-line summary of the worst."""
    problems = []
    worst_file = (0, "-")
    worst_section = (0, "-")

    targets = [DOCS_INDEX] + sorted(DOCS_DIR.glob("*.md"))
    for path in targets:
        if not path.exists():
            continue
        rel = path.relative_to(ROOT).as_posix()
        lines = path.read_text(encoding="utf-8").splitlines()

        if len(lines) > worst_file[0]:
            worst_file = (len(lines), rel)
        if len(lines) > FILE_BUDGET:
            problems.append(
                "{0}: {1} lines (budget {2})".format(rel, len(lines), FILE_BUDGET)
            )

        heads = [i for i, ln in enumerate(lines) if HEADING.match(ln)]
        for n, start in enumerate(heads):
            end = heads[n + 1] if n + 1 < len(heads) else len(lines)
            size = end - start
            where = "{0} :: {1}".format(rel, lines[start].strip())
            if size > worst_section[0]:
                worst_section = (size, where)
            if size > SECTION_BUDGET:
                problems.append(
                    "{0}: {1} lines (budget {2})".format(where, size, SECTION_BUDGET)
                )

    summary = "largest file {0} lines ({1}), largest section {2} lines ({3})".format(
        worst_file[0], worst_file[1], worst_section[0], worst_section[1]
    )
    return problems, summary


class Report:
    """Collects one row per check; failure output is printed once, at the end."""

    def __init__(self):
        self.rows = []
        self.failures = []

    def add(self, name, ok, note="", detail=""):
        self.rows.append((name, ok, note))
        if not ok:
            self.failures.append((name, detail or note))

    @property
    def ok(self):
        return not self.failures

    def emit(self):
        for name, detail in self.failures:
            print("\n----- {0} -----".format(name))
            print(detail.rstrip() or "(no output)")
        print()
        width = max([len(n) for n, _, _ in self.rows] or [0])
        for name, ok, note in self.rows:
            status = "ok" if ok else "FAILED"
            print("  {0:<{1}}  {2}  {3}".format(name, width, status, note).rstrip())
        if self.ok:
            print("\nVERIFY OK")
        else:
            print(
                "\nVERIFY FAILED ({0} of {1})".format(
                    len(self.failures), len(self.rows)
                )
            )


def check_build(report, timeout):
    started = time.monotonic()
    status, output = run(["dotnet", "build", SOLUTION], timeout)
    secs = "{0:.0f}s".format(time.monotonic() - started)
    report.add("build", status == 0, secs, output)


def check_import(report, godot, timeout):
    started = time.monotonic()
    status, output = run([godot, "--headless", "--path", ".", "--import"], timeout)
    secs = "{0:.0f}s".format(time.monotonic() - started)
    why = godot_complaint(status, output)
    if why is None:
        report.add("import", True, secs)
    else:
        report.add("import", False, why.splitlines()[0], why + "\n\n" + output)


def check_test(report, godot, name, timeout):
    scene = "res://scenes/dev/{0}.tscn".format(name)
    started = time.monotonic()
    status, output = run([godot, "--headless", "--path", ".", scene], timeout)
    secs = "{0:.0f}s".format(time.monotonic() - started)
    why = godot_complaint(status, output)
    if why is None:
        verdicts = [ln.strip() for ln in output.splitlines() if VERDICT.search(ln)]
        note = "{0}  {1}".format(verdicts[-1][:60], secs) if verdicts else secs
        report.add(name, True, note)
    else:
        report.add(name, False, why.splitlines()[0], why + "\n\n" + output)


def check_docs(report):
    problems, summary = doc_budget()
    if problems:
        report.add(
            "docs", False, "{0} over budget".format(len(problems)), "\n".join(problems)
        )
    else:
        report.add("docs", True, summary)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Build, import, smoke tests and doc budget in one verdict.",
        epilog="With no flags, everything runs. Any flag selects only what it names.",
    )
    parser.add_argument("--build", action="store_true", help="dotnet build only")
    parser.add_argument(
        "--import",
        dest="do_import",
        action="store_true",
        help="Godot asset import only",
    )
    parser.add_argument("--tests", action="store_true", help="every smoke test")
    parser.add_argument(
        "--test",
        metavar="NAME",
        action="append",
        default=[],
        help="one smoke test by scene name; repeatable",
    )
    parser.add_argument("--docs", action="store_true", help="doc budget only")
    parser.add_argument(
        "--no-import",
        dest="skip_import",
        action="store_true",
        help="skip the import that precedes smoke tests (CI imports separately)",
    )
    parser.add_argument(
        "--timeout",
        type=int,
        default=DEFAULT_TIMEOUT,
        metavar="SECONDS",
        help="per-command limit (default {0})".format(DEFAULT_TIMEOUT),
    )
    args = parser.parse_args(argv)

    known = smoke_tests()
    unknown = sorted(set(args.test) - set(known))
    if unknown:
        parser.error("no such smoke test scene: {0}".format(", ".join(unknown)))

    selected = args.build or args.do_import or args.tests or args.test or args.docs
    want_build = args.build or not selected
    want_docs = args.docs or not selected
    tests = args.test or (known if (args.tests or not selected) else [])
    # Smoke tests need the assets imported; .godot/ is gitignored, so a fresh
    # checkout has none and the scenes would log load errors while exiting 0.
    want_import = args.do_import or (bool(tests) and not args.skip_import)

    godot = godot_binary() if (want_import or tests) else ""
    report = Report()

    if want_build:
        check_build(report, args.timeout)
    if want_import:
        check_import(report, godot, args.timeout)
    for name in tests:
        check_test(report, godot, name, args.timeout)
    if want_docs:
        check_docs(report)

    report.emit()
    return 0 if report.ok else 1


if __name__ == "__main__":
    # Godot and MSBuild both emit non-ASCII; a cp1252 stdout would raise on it.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    sys.exit(main())
