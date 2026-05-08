#!/usr/bin/env sh
# Layer 3 - System review monitor entry point.
#
# Runs the system-review skill against the stable instance from outside the
# app. Read-only. Writes one Markdown review file to:
#   <workspace>/logs/system-review/<date>-<time>.md
#
# Defaults assume the agent-taskboard-devspace layout. Override via env vars:
#   ATP_WORKSPACE        - workspace root (default: C:/Projects/agent-taskboard-workspace)
#   ATP_STABLE_CHECKOUT  - stable repo (default: C:/Projects/agent-taskboard-devspace/agent-taskboard-stable)
#   ATP_LOOKBACK_HOURS   - lookback window for activity (default: 8)
#   ATP_CLI              - CLI to drive the review (default: claude)
#
# Usage:
#   ./scripts/supervisor/run-system-review.sh              # full CLI-driven review
#   ATP_LOOKBACK_HOURS=24 ./scripts/supervisor/run-system-review.sh
#
#   # Dry-run: skip the CLI, run only the structured bus health checks.
#   # Useful for CI, post-incident triage, or proving the bus integration.
#   ./scripts/supervisor/run-system-review.sh --dry-run
#   ./scripts/supervisor/run-system-review.sh --dry-run --fixture path/to/bus.jsonl
#
# This is intentionally simple. Schedule via cron / Task Scheduler / a manual
# habit. The skill itself lives next to this script as system-review.md.

set -eu

WORKSPACE="${ATP_WORKSPACE:-C:/Projects/agent-taskboard-workspace}"
STABLE="${ATP_STABLE_CHECKOUT:-C:/Projects/agent-taskboard-devspace/agent-taskboard-stable}"
LOOKBACK="${ATP_LOOKBACK_HOURS:-8}"
CLI="${ATP_CLI:-claude}"

DRY_RUN=0
FIXTURE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY_RUN=1; shift ;;
    --fixture) DRY_RUN=1; FIXTURE="$2"; shift 2 ;;
    -h|--help)
      sed -n '1,30p' "$0"; exit 0 ;;
    *)
      echo "unknown argument: $1" >&2; exit 3 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SKILL_FILE="$SCRIPT_DIR/system-review.md"
HEALTH_SCRIPT="$SCRIPT_DIR/system-health-check.mjs"
OUT_DIR="$WORKSPACE/logs/system-review"
STAMP="$(date -u +%Y-%m-%d-%H%M)"
OUT_FILE="$OUT_DIR/$STAMP.md"

if [ ! -f "$SKILL_FILE" ]; then
  echo "skill file missing: $SKILL_FILE" >&2
  exit 2
fi

mkdir -p "$OUT_DIR"

# If a review with this exact minute already exists, append a -2 suffix per
# the skill spec. Cheap: we only check the immediate stamp.
if [ -e "$OUT_FILE" ]; then
  OUT_FILE="$OUT_DIR/$STAMP-2.md"
fi

# ---- Dry-run mode ----------------------------------------------------
# The dry-run path skips the CLI session entirely and runs only the
# structured bus health checks. It is the fastest way to prove the
# Layer 3 monitor can read bus-shaped evidence (the deliverable that
# does not depend on the CLI being installed).
if [ "$DRY_RUN" -eq 1 ]; then
  if [ ! -f "$HEALTH_SCRIPT" ]; then
    echo "health-check script missing: $HEALTH_SCRIPT" >&2
    exit 2
  fi
  if ! command -v node >/dev/null 2>&1; then
    echo "node not found on PATH; required for --dry-run" >&2
    exit 2
  fi
  echo "[dry-run] writing $OUT_FILE" >&2
  if [ -n "$FIXTURE" ]; then
    exec node "$HEALTH_SCRIPT" --fixture "$FIXTURE" --stable "$STABLE" --out "$OUT_FILE"
  else
    exec node "$HEALTH_SCRIPT" --workspace "$WORKSPACE" --stable "$STABLE" --out "$OUT_FILE"
  fi
fi

# ---- Full CLI-driven review -----------------------------------------
PROMPT="$(cat <<EOF
Read the skill at $SKILL_FILE and produce a system review.

Environment:
  ATP_WORKSPACE=$WORKSPACE
  ATP_STABLE_CHECKOUT=$STABLE
  ATP_LOOKBACK_HOURS=$LOOKBACK

Before writing the prose review, run the structured bus health checks:

  node $HEALTH_SCRIPT --workspace $WORKSPACE --stable $STABLE --json

The JSON output covers the eight checks listed in the skill (long silent
periods, repeated interventions, repeated failed/cancelled runs, token
spikes, supporting jobs without accepted review, stuck loops, weak review
evidence, backend crash markers). Embed the findings under the "Health
findings (bus-driven)" section of the review verbatim, severity-sorted,
preserving the msg= / job= / run= / artifacts= references so the operator
can drill down. Add prose context for anything that needs interpretation
beyond the structured finding.

Write the review to: $OUT_FILE

Read-only against the stable checkout and the workspace; do not modify any
project source tree or any job folder. Your only write is the single
review file at the path above.

End with [[TASK_DONE]] plus the path of the review file.
EOF
)"

case "$CLI" in
  claude)
    exec claude --dangerously-skip-permissions -p "$PROMPT"
    ;;
  codex)
    exec codex exec "$PROMPT"
    ;;
  copilot)
    exec copilot --allow-all-tools -p "$PROMPT"
    ;;
  *)
    echo "unsupported CLI: $CLI" >&2
    exit 3
    ;;
esac
