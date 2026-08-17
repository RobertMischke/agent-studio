#!/usr/bin/env bash

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
updater=$script_dir/update-stable.sh
probe=$script_dir/stable-frontend-boot-probe.mjs
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t update-stable)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

remote=$test_root/remote.git
source_checkout=$test_root/source
stable_checkout=$test_root/stable
devspace=$test_root/devspace
fake_bin=$test_root/bin
stop_marker=$test_root/stop-ran
start_marker=$test_root/start-ran
service_log=$test_root/service-order.log

git init --bare --quiet "$remote"
git init --quiet "$source_checkout"
git -C "$source_checkout" config user.name 'Stable Update Test'
git -C "$source_checkout" config user.email 'stable-update@example.invalid'
mkdir -p "$source_checkout/frontend/scripts"
printf '%s\n' '/frontend/node_modules/' '/frontend/.angular/' > "$source_checkout/.gitignore"
printf '%s\n' '{"name":"fixture","private":true}' > "$source_checkout/frontend/package.json"
printf '%s\n' '{"lockfileVersion":3}' > "$source_checkout/frontend/package-lock.json"
printf '%s\n' '// initial compatibility patch' > "$source_checkout/frontend/scripts/patch-coding-agent-chat-technical-blocks.mjs"
printf '%s\n' 'base release' > "$source_checkout/release.txt"
git -C "$source_checkout" add .
git -C "$source_checkout" commit --quiet -m 'base release'
git -C "$source_checkout" branch -M main
git -C "$source_checkout" remote add origin "$remote"
git -C "$source_checkout" push --quiet -u origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
git clone --quiet "$remote" "$stable_checkout"

mkdir -p \
  "$devspace" \
  "$fake_bin" \
  "$stable_checkout/frontend/node_modules/playwright-core" \
  "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022" \
  "$stable_checkout/frontend/.angular/cache"
printf '%s\n' stale-prebundle > "$stable_checkout/frontend/.angular/cache/deps.js"
printf '%s\n' unpatched > "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs"

cat > "$stable_checkout/frontend/node_modules/playwright-core/package.json" <<'EOF'
{"name":"playwright-core","version":"0.0.0-test","main":"index.cjs"}
EOF
cat > "$stable_checkout/frontend/node_modules/playwright-core/index.cjs" <<'EOF'
const { EventEmitter } = require('node:events');
const { existsSync } = require('node:fs');

module.exports = {
  chromium: {
    async launch() {
      return {
        async newPage() {
          const page = new EventEmitter();
          page.goto = async () => {
            if (existsSync(process.env.ATP_TEST_STALE_CACHE)) {
              page.emit('pageerror', new Error(
                "coding-agent-chat_markdown.js does not provide an export named 'protectTechnicalMarkdown'",
              ));
            }
            if (process.env.ATP_TEST_PAGEERROR) {
              page.emit('pageerror', new Error(process.env.ATP_TEST_PAGEERROR));
            }
            return { ok: () => true, status: () => 200 };
          };
          return page;
        },
        async close() {},
      };
    },
  },
};
EOF

cat > "$devspace/stop-stable.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_STOP_MARKER"
[ -z "${ATP_TEST_SERVICE_LOG:-}" ] || printf '%s\n' stable-stop >> "$ATP_TEST_SERVICE_LOG"
EOF
cat > "$devspace/start-stable.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_START_MARKER"
[ -z "${ATP_TEST_SERVICE_LOG:-}" ] || printf '%s\n' stable-start >> "$ATP_TEST_SERVICE_LOG"
EOF
cat > "$fake_bin/npm" <<'EOF'
#!/usr/bin/env sh
set -eu
printf '%s\n' patched > "$ATP_TEST_PACKAGE_FILE"
EOF
cat > "$test_root/task-server-control" <<'EOF'
#!/usr/bin/env sh
set -eu
action=
while [ "$#" -gt 0 ]; do
  if [ "$1" = "-Action" ]; then
    action=$2
    shift 2
    continue
  fi
  shift
done
printf 'task-server-%s\n' "$(printf '%s' "$action" | tr '[:upper:]' '[:lower:]')" >> "$ATP_TEST_SERVICE_LOG"
EOF
cat > "$test_root/task-server-install" <<'EOF'
#!/usr/bin/env sh
set -eu
printf '%s\n' task-server-install >> "$ATP_TEST_SERVICE_LOG"
EOF
cat > "$fake_bin/curl" <<'EOF'
#!/usr/bin/env sh
set -eu
url=
for arg in "$@"; do
  case "$arg" in http://*) url=$arg ;; esac
done
case "$url" in
  */api/v1/protocol) printf '%s\n' '{"current":3}' ;;
  */api/v1/management/status) printf '{"serverVersion":"%s"}\n' "${ATP_TEST_TASK_SERVER_VERSION:-old-release}" ;;
  */api/v1/management/prepare-shutdown) printf '%s\n' '{"safeToStop":true}' ;;
  */api/v1/management/backups) printf '%s\n' '{"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}' ;;
  *) printf '%s\n' '{}' ;;
esac
EOF
chmod +x \
  "$devspace/stop-stable.sh" \
  "$devspace/start-stable.sh" \
  "$fake_bin/npm" \
  "$fake_bin/curl" \
  "$test_root/task-server-control" \
  "$test_root/task-server-install"

run_update() {
  env \
    ATP_DEVSPACE_DIR="$devspace" \
    ATP_STABLE_CHECKOUT="$stable_checkout" \
    ATP_STOP_SCRIPT="$devspace/stop-stable.sh" \
    ATP_START_SCRIPT="$devspace/start-stable.sh" \
    ATP_BOOT_PROBE_SCRIPT="$probe" \
    ATP_BOOT_PROBE_SETTLE_MS=0 \
    ATP_TEST_STOP_MARKER="$stop_marker" \
    ATP_TEST_START_MARKER="$start_marker" \
    ATP_TEST_PACKAGE_FILE="$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs" \
    ATP_TEST_STALE_CACHE="$stable_checkout/frontend/.angular/cache/deps.js" \
    ATP_TEST_SERVICE_LOG="$service_log" \
    PATH="$fake_bin:$PATH" \
    "$updater" 2>&1
}

# A changed postinstall patch causes npm install. The updater must invalidate
# the old optimizer prebundle before the browser probe observes the new export.
printf '%s\n' '// patched compatibility bridge' > "$source_checkout/frontend/scripts/patch-coding-agent-chat-technical-blocks.mjs"
git -C "$source_checkout" commit --quiet -am 'update compatibility patch'
git -C "$source_checkout" push --quiet origin main

output=$(run_update)
test -f "$stop_marker"
test -f "$start_marker"
test ! -e "$stable_checkout/frontend/.angular/cache"
grep -q '^patched$' "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs"
printf '%s' "$output" | grep -q 'Invalidated the Angular/Vite optimizer cache'
printf '%s' "$output" | grep -q 'Boot completed without page errors'
printf '%s' "$output" | grep -q 'Stable started and healthy'

# A separate release injects an application boot crash. An open port and a
# successful document response must not allow the updater to claim health.
printf '%s\n' 'release with injected crash' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'release with boot crash'
git -C "$source_checkout" push --quiet origin main

set +e
crash_output=$(ATP_TEST_PAGEERROR='injected boot crash' run_update)
crash_rc=$?
set -e

test "$crash_rc" -ne 0
printf '%s' "$crash_output" | grep -q 'PAGEERROR'
printf '%s' "$crash_output" | grep -q 'injected boot crash'
if printf '%s' "$crash_output" | grep -q 'Stable started and healthy'; then
  printf '%s\n' 'updater reported health after an injected page error' >&2
  exit 1
fi

# A detached rollback target is upgraded in a verification state. The Task
# Server package is installed and started before the Stable API, and the
# checkout is re-attached to main only after all probes pass.
printf '%s\n' 'release with standalone task server' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'release with standalone task server'
git -C "$source_checkout" push --quiet origin main
target=$(git -C "$source_checkout" rev-parse HEAD)
: > "$service_log"

output=$( \
  ATP_TASK_SERVER_REQUIRED=1 \
  ATP_TASK_SERVER_INSTALL_SCRIPT="$test_root/task-server-install" \
  ATP_TASK_SERVER_CONTROL_SCRIPT="$test_root/task-server-control" \
  ATP_TASK_SERVER_URL=http://task-server.invalid \
  ATP_STABLE_API_URL=http://stable-api.invalid \
  run_update)

test "$(git -C "$stable_checkout" rev-parse HEAD)" = "$target"
test "$(git -C "$stable_checkout" symbolic-ref --short HEAD)" = main
test "$(cat "$service_log")" = "$(printf '%s\n' \
  stable-stop \
  task-server-stop \
  task-server-install \
  task-server-start \
  stable-start)"
printf '%s' "$output" | grep -q 'Probing Task Server, Stable proxy, API, and board projection'
printf '%s' "$output" | grep -q 'Attaching verified Stable checkout to main'

# A package that already matches the candidate remains online while Stable is
# replaced. This is the initial cutover path after migration and avoids a
# needless authority interruption between import and proxy activation.
printf '%s\n' 'release with unchanged task server package' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'release with unchanged task server package'
git -C "$source_checkout" push --quiet origin main
target=$(git -C "$source_checkout" rev-parse HEAD)
: > "$service_log"

output=$( \
  ATP_TASK_SERVER_REQUIRED=1 \
  ATP_TASK_SERVER_INSTALL_SCRIPT="$test_root/task-server-install" \
  ATP_TASK_SERVER_CONTROL_SCRIPT="$test_root/task-server-control" \
  ATP_TASK_SERVER_URL=http://task-server.invalid \
  ATP_STABLE_API_URL=http://stable-api.invalid \
  ATP_TEST_TASK_SERVER_VERSION="$target" \
  run_update)

test "$(git -C "$stable_checkout" rev-parse HEAD)" = "$target"
test "$(cat "$service_log")" = "$(printf '%s\n' stable-stop stable-start)"
printf '%s' "$output" | grep -q "Supervised Task Server already matches candidate $target"
printf '%s' "$output" | grep -q 'Waiting for preserved Task Server before Stable API startup'

printf '%s\n' 'update-stable tests passed'
