#!/usr/bin/env bash
#
# Runs Godot headless from the project root and fails on anything the engine
# complains about. Usage: run_godot.sh --import
#                        run_godot.sh res://scenes/dev/CameraSmokeTest.tscn
#
# The exit code alone is not a sufficient gate. A scene whose resources failed
# to import still quits 0 while logging the failure, so a run can be green over
# a broken load. Warnings are fatal because every one the codebase raises marks
# a real defect -- a stranded machine, a negative starting balance, a scene
# missing its Simulation -- so one showing up in a passing run is news either
# way. That rule is a live constraint on the warnings themselves: a warning a
# smoke test is expected to trip is by definition not reporting a defect, and
# gets narrowed at the source rather than filtered out here.
set -uo pipefail

log="$(mktemp)" || {
  echo "::error::could not create a log file for godot $*"
  exit 1
}
trap 'rm -f "$log"' EXIT

"$GODOT" --headless --path . "$@" 2>&1 | tee "$log"
# Copy the whole array in one go: reading a single element is itself a command,
# and that resets PIPESTATUS before the second element can be read.
statuses=("${PIPESTATUS[@]}")
godot_status="${statuses[0]}"
capture_status="${statuses[1]}"

# Godot's own verdict is read first, so a genuine test failure keeps its exit
# status even if something went wrong alongside it.
if [ "$godot_status" -ne 0 ]; then
  echo "::error::godot $* exited with status $godot_status"
  exit "$godot_status"
fi

# Godot was happy, but the log is what every check below reads. If tee could
# not write it, an unwritten log would pass the grep and report a clean run
# nobody actually looked at — the same false pass this wrapper exists to stop.
if [ "$capture_status" -ne 0 ]; then
  echo "::error::could not capture godot output for godot $* (tee exited $capture_status)"
  exit 1
fi

# Strip ANSI colour before anchoring, or an escape sequence hides the marker.
complaints="$(sed -e 's/\x1b\[[0-9;]*m//g' "$log" \
  | grep -En '^(ERROR|WARNING|SCRIPT ERROR):' || true)"

if [ -n "$complaints" ]; then
  echo "::error::godot $* exited 0 but the engine logged:"
  echo "$complaints"
  exit 1
fi
