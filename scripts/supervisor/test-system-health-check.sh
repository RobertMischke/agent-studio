#!/usr/bin/env sh
# Smoke test for system-health-check.mjs.
#
# Runs the dry-run against the bundled sample fixture and asserts that
# every documented health check produces at least one finding. This is
# the deliverable that proves the Layer 3 monitor can read bus-shaped
# evidence without depending on the live bus store.
#
# Exits non-zero on any missing finding and prints the offending check
# so a CI run fails noisily. Skips gracefully when node is not on PATH.

set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
HEALTH_SCRIPT="$SCRIPT_DIR/system-health-check.mjs"
FIXTURE="$SCRIPT_DIR/fixtures/sample-bus.jsonl"

if ! command -v node >/dev/null 2>&1; then
  echo "node not on PATH; skipping" >&2
  exit 0
fi
if [ ! -f "$FIXTURE" ]; then
  echo "fixture missing: $FIXTURE" >&2
  exit 2
fi

OUT="$(node "$HEALTH_SCRIPT" --fixture "$FIXTURE" --json)"

# Each expected check string must appear at least once in the JSON
# findings array. The fixture is hand-crafted so the eight checks all
# fire; if a contributor breaks the script or the fixture, this list
# is what fails first.
EXPECTED="long-silent-period repeated-interventions repeated-failed-runs token-spike supporting-without-review stuck-loop weak-review-evidence backend-crash"

failed=0
for check in $EXPECTED; do
  if ! printf '%s' "$OUT" | grep -q "\"check\": \"$check\""; then
    echo "FAIL: no finding for check=$check" >&2
    failed=1
  fi
done

# Verdict must be "Action needed" given the High-severity findings.
if ! printf '%s' "$OUT" | grep -q '"verdict": "Action needed"'; then
  echo "FAIL: verdict not 'Action needed'" >&2
  failed=1
fi

if [ "$failed" -ne 0 ]; then
  echo "--- raw output ---" >&2
  printf '%s\n' "$OUT" >&2
  exit 1
fi

echo "OK: all 8 health checks produced findings; verdict=Action needed"
