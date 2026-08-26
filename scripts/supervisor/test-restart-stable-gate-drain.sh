#!/usr/bin/env sh
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WATCHER="$SCRIPT_DIR/restart-stable-after-batch.sh"
TEST_ROOT="$(mktemp -d 2>/dev/null || mktemp -d -t stable-gate-drain)"
trap 'rm -rf "$TEST_ROOT"' EXIT

WORKSPACE="$TEST_ROOT/workspace"
STABLE="$TEST_ROOT/stable"
FAKE_BIN="$TEST_ROOT/bin"
UPDATE="$TEST_ROOT/update-stable.sh"
LANE="$WORKSPACE/projects/fixture/4-auto-review"
STATE="$WORKSPACE/logs/stable-restart-watcher"
PROBE_COUNT="$TEST_ROOT/gate-probes"
UPDATE_CALLED="$TEST_ROOT/update-called"

mkdir -p "$STABLE" "$FAKE_BIN" "$LANE" "$STATE"
: > "$STATE/snapshot.txt"
mkdir -p "$LANE/new-card"

cat > "$UPDATE" <<'EOF'
#!/usr/bin/env sh
: > "$ATP_TEST_UPDATE_CALLED"
exit 1
EOF
chmod +x "$UPDATE"

cat > "$FAKE_BIN/curl" <<'EOF'
#!/usr/bin/env sh
for arg in "$@"; do url="$arg"; done
case "$url" in
  */api/runner/status)
    printf '%s\n' '[{"activeJobId":null}]'
    ;;
  */healthz/drain)
    count=0
    [ ! -r "$ATP_TEST_PROBE_COUNT" ] || count="$(cat "$ATP_TEST_PROBE_COUNT")"
    count=$((count + 1))
    printf '%s\n' "$count" > "$ATP_TEST_PROBE_COUNT"
    if [ "$ATP_TEST_GATE_MODE" = "release" ] && [ "$count" -ge 2 ]; then
      printf '%s\n' idle
    else
      printf '%s\n' gate-busy
    fi
    ;;
  *)
    exit 22
    ;;
esac
EOF
chmod +x "$FAKE_BIN/curl"

run_watcher() {
  ATP_WORKSPACE="$WORKSPACE" \
  ATP_PROJECT=fixture \
  ATP_STABLE_CHECKOUT="$STABLE" \
  ATP_STABLE_API=http://stable.invalid \
  ATP_RESTART_THRESHOLD=1 \
  ATP_UPDATE_SCRIPT="$UPDATE" \
  ATP_GATE_DRAIN_TIMEOUT_SECONDS=1 \
  ATP_GATE_DRAIN_POLL_SECONDS=1 \
  ATP_TASK_SERVER_REQUIRED=0 \
  ATP_TEST_PROBE_COUNT="$PROBE_COUNT" \
  ATP_TEST_UPDATE_CALLED="$UPDATE_CALLED" \
  ATP_TEST_GATE_MODE="$1" \
  PATH="$FAKE_BIN:$PATH" \
    sh "$WATCHER" 2>&1
}

output="$(run_watcher release)"
test -f "$UPDATE_CALLED"
test "$(cat "$PROBE_COUNT")" -ge 2
printf '%s' "$output" | grep -q "merge gate busy"
printf '%s' "$output" | grep -q "merge gate idle"

rm -f "$PROBE_COUNT" "$UPDATE_CALLED"
rm -rf "$LANE/new-card"
: > "$STATE/snapshot.txt"
mkdir -p "$LANE/timeout-card"

output="$(run_watcher timeout)"
test -f "$UPDATE_CALLED"
test "$(cat "$PROBE_COUNT")" -ge 2
printf '%s' "$output" | grep -q "drain window exhausted"

echo "stable merge-gate drain tests passed"
