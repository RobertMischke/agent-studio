#!/usr/bin/env sh
# Layer 3 - Long-running entry point for the stable restart watcher.
#
# Wraps restart-stable-after-batch.sh in a sleep-tick loop. This is the script
# the user (or a host scheduler) starts manually. It deliberately does not
# daemonise itself: same convention as run-system-review.sh — keep the
# scheduling decision outside the dev tree.
#
# The inner script is responsible for one decision per tick. This wrapper
# only controls cadence and survives transient errors (network blips, brief
# stable downtimes during the very restart we just triggered).
#
# Usage:
#   ./scripts/supervisor/run-stable-restart-watcher.sh
#
# Env vars (see restart-stable-after-batch.sh for the rest):
#   ATP_RESTART_TICK_SECONDS  poll interval (default: 60)

set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
INNER="$SCRIPT_DIR/restart-stable-after-batch.sh"
TICK="${ATP_RESTART_TICK_SECONDS:-60}"

if [ ! -x "$INNER" ]; then
  echo "[restart-watcher] ERROR: $INNER missing or not executable" >&2
  exit 2
fi

trap 'echo "[restart-watcher] stopping"; exit 0' INT TERM

echo "[restart-watcher] starting; tick=${TICK}s; inner=$INNER"

while :; do
  # The inner script swallows operational errors and returns 0 for a
  # handled tick; a non-zero exit is fatal misconfiguration. Don't re-run
  # if the inner script asks us to stop.
  if ! "$INNER"; then
    rc=$?
    echo "[restart-watcher] inner script exited $rc — stopping" >&2
    exit "$rc"
  fi
  sleep "$TICK"
done
