#!/usr/bin/env bash

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
watcher="$script_dir/restart-stable-after-batch.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t stable-main-advance)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

remote="$test_root/remote.git"
source_checkout="$test_root/source"
stable="$test_root/stable"
workspace="$test_root/workspace"
fake_bin="$test_root/bin"
update="$test_root/update-stable.sh"
update_count="$test_root/update-count"
busy_file="$test_root/stable-busy"
task_server_down="$test_root/task-server-down"
task_server_control="$test_root/task-server-control"
task_server_control_count="$test_root/task-server-control-count"

git init --bare --quiet "$remote"
git init --quiet "$source_checkout"
git -C "$source_checkout" config user.name 'Deploy Cron Test'
git -C "$source_checkout" config user.email 'deploy-cron@example.invalid'
printf '%s\n' base > "$source_checkout/release.txt"
git -C "$source_checkout" add release.txt
git -C "$source_checkout" commit --quiet -m 'base release'
git -C "$source_checkout" branch -M main
git -C "$source_checkout" remote add origin "$remote"
git -C "$source_checkout" push --quiet -u origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
git clone --quiet "$remote" "$stable"

mkdir -p "$fake_bin" "$workspace"
printf '%s\n' 0 > "$update_count"
printf '%s\n' 0 > "$task_server_control_count"

cat > "$update" <<'EOF'
#!/usr/bin/env sh
set -eu
count=$(cat "$ATP_TEST_UPDATE_COUNT")
printf '%s\n' $((count + 1)) > "$ATP_TEST_UPDATE_COUNT"
git -C "$ATP_STABLE_CHECKOUT" fetch --quiet origin main
git -C "$ATP_STABLE_CHECKOUT" reset --hard --quiet origin/main
EOF
chmod +x "$update"

cat > "$fake_bin/curl" <<'EOF'
#!/usr/bin/env sh
set -eu
url=
writes_code=0
for arg in "$@"; do
  [ "$arg" != "%{http_code}" ] || writes_code=1
  case "$arg" in http://*) url=$arg ;; esac
done
case "$url" in
  http://task-server.invalid/readyz)
    if [ -f "$ATP_TEST_TASK_SERVER_DOWN" ]; then
      exit 22
    fi
    ;;
  */healthz/drain)
    printf '%s\n' idle
    ;;
  */healthz)
    [ "$writes_code" -eq 0 ] || printf '%s' 200
    ;;
  */api/runner/*/mode)
    [ "$writes_code" -eq 0 ] || printf '%s' 200
    ;;
  */api/runner/status)
    if [ -f "$ATP_TEST_BUSY_FILE" ]; then
      printf '%s\n' '{"fixture":{"mode":"auto-continuous","activeJobId":"AGT-test"}}'
    else
      printf '%s\n' '{"fixture":{"mode":"auto-continuous","activeJobId":null}}'
    fi
    ;;
  *)
    printf 'unexpected curl URL: %s\n' "$url" >&2
    exit 22
    ;;
esac
EOF
chmod +x "$fake_bin/curl"

cat > "$task_server_control" <<'EOF'
#!/usr/bin/env sh
set -eu
count=$(cat "$ATP_TEST_TASK_SERVER_CONTROL_COUNT")
printf '%s\n' $((count + 1)) > "$ATP_TEST_TASK_SERVER_CONTROL_COUNT"
rm -f "$ATP_TEST_TASK_SERVER_DOWN"
EOF
chmod +x "$task_server_control"

run_tick() {
  env \
    ATP_WORKSPACE="$workspace" \
    ATP_PROJECT=fixture \
    ATP_STABLE_CHECKOUT="$stable" \
    ATP_STABLE_API=http://stable.invalid \
    ATP_RESTART_TRIGGER=main-advance \
    ATP_UPDATE_SCRIPT="$update" \
    ATP_TEST_UPDATE_COUNT="$update_count" \
    ATP_CLIENT_ID=deploy-cron-test \
    ATP_HEALTHZ_TIMEOUT=1 \
    ATP_RESUME_MAX_ATTEMPTS=1 \
    ATP_TEST_BUSY_FILE="$busy_file" \
    ATP_TASK_SERVER_REQUIRED=1 \
    ATP_TASK_SERVER_URL=http://task-server.invalid \
    ATP_TASK_SERVER_CONTROL_SCRIPT="$task_server_control" \
    ATP_TEST_TASK_SERVER_DOWN="$task_server_down" \
    ATP_TEST_TASK_SERVER_CONTROL_COUNT="$task_server_control_count" \
    PATH="$fake_bin:$PATH" \
    sh "$watcher" 2>&1
}

# No main movement is a clean cron no-op.
output=$(run_tick)
test "$(cat "$update_count")" = 0
printf '%s' "$output" | grep -q 'stable already matches origin/main'

# The watcher heals the independent Task Server through its supervisor even
# when no Stable deployment is pending.
touch "$task_server_down"
output=$(run_tick)
test "$(cat "$task_server_control_count")" = 1
printf '%s' "$output" | grep -q 'Task Server recovered through its supervised service boundary'

# A promoted main is deployed once and recorded with its exact target SHA.
printf '%s\n' promoted > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'promoted release'
git -C "$source_checkout" push --quiet origin main
promoted_sha=$(git -C "$source_checkout" rev-parse HEAD)
output=$(run_tick)
test "$(cat "$update_count")" = 1
test "$(git -C "$stable" rev-parse HEAD)" = "$promoted_sha"
printf '%s' "$output" | grep -q 'trigger=main-advance'
grep -q '"trigger":"main-advance"' "$workspace/logs/stable-restarts.jsonl"
grep -q "\"targetMain\":\"$promoted_sha\"" "$workspace/logs/stable-restarts.jsonl"

# Busy stable defers the next promoted SHA without calling the updater.
printf '%s\n' promoted-again > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'next release'
git -C "$source_checkout" push --quiet origin main
touch "$busy_file"
output=$(run_tick)
test "$(cat "$update_count")" = 1
printf '%s' "$output" | grep -q 'stable has an active job; skipping'

printf '%s\n' 'stable main-advance deploy tests passed'
