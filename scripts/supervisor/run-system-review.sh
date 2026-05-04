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
#   ./scripts/supervisor/run-system-review.sh
#   ATP_LOOKBACK_HOURS=24 ./scripts/supervisor/run-system-review.sh
#
# This is intentionally simple. Schedule via cron / Task Scheduler / a manual
# habit. The skill itself lives next to this script as system-review.md.

set -eu

WORKSPACE="${ATP_WORKSPACE:-C:/Projects/agent-taskboard-workspace}"
STABLE="${ATP_STABLE_CHECKOUT:-C:/Projects/agent-taskboard-devspace/agent-taskboard-stable}"
LOOKBACK="${ATP_LOOKBACK_HOURS:-8}"
CLI="${ATP_CLI:-claude}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SKILL_FILE="$SCRIPT_DIR/system-review.md"
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

PROMPT="$(cat <<EOF
Read the skill at $SKILL_FILE and produce a system review.

Environment:
  ATP_WORKSPACE=$WORKSPACE
  ATP_STABLE_CHECKOUT=$STABLE
  ATP_LOOKBACK_HOURS=$LOOKBACK

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
