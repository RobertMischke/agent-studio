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
git -C "$stable_checkout" switch --quiet --detach

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
EOF
cat > "$devspace/start-stable.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_START_MARKER"
EOF
cat > "$fake_bin/npm" <<'EOF'
#!/usr/bin/env sh
set -eu
printf '%s\n' patched > "$ATP_TEST_PACKAGE_FILE"
EOF
chmod +x "$devspace/stop-stable.sh" "$devspace/start-stable.sh" "$fake_bin/npm"

run_update() {
  env \
    ATP_DEVSPACE_DIR="$devspace" \
    ATP_STABLE_CHECKOUT="$stable_checkout" \
    ATP_STOP_SCRIPT="$devspace/stop-stable.sh" \
    ATP_START_SCRIPT="$devspace/start-stable.sh" \
    ATP_BOOT_PROBE_SCRIPT="$probe" \
    ATP_BOOT_PROBE_TIMEOUT_MS="${ATP_TEST_BOOT_PROBE_TIMEOUT_MS:-180000}" \
    ATP_BOOT_PROBE_SETTLE_MS=0 \
    ATP_TASK_SERVER_REQUIRED="${ATP_TEST_TASK_SERVER_REQUIRED:-0}" \
    ATP_TASK_SERVER_INSTALL="${ATP_TEST_TASK_SERVER_INSTALL:-auto}" \
    ATP_TEST_STOP_MARKER="$stop_marker" \
    ATP_TEST_START_MARKER="$start_marker" \
    ATP_TEST_PACKAGE_FILE="$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs" \
    ATP_TEST_STALE_CACHE="$stable_checkout/frontend/.angular/cache/deps.js" \
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
test "$(git -C "$stable_checkout" branch --show-current)" = main
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

# A decoupled release must never start the API when the required standalone
# authority is absent. Keep this timeout small because the endpoint is
# intentionally unreachable in the fixture.
printf '%s\n' 'release requiring task server' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'require standalone task server'
git -C "$source_checkout" push --quiet origin main
rm -f "$start_marker"

set +e
task_server_output=$(ATP_TEST_TASK_SERVER_REQUIRED=1 ATP_TEST_TASK_SERVER_INSTALL=0 \
  ATP_TEST_BOOT_PROBE_TIMEOUT_MS=1000 run_update)
task_server_rc=$?
set -e

test "$task_server_rc" -ne 0
test ! -e "$start_marker"
printf '%s' "$task_server_output" | grep -q 'Timed out waiting for required endpoint'
if printf '%s' "$task_server_output" | grep -q 'Stable started and healthy'; then
  printf '%s\n' 'updater reported health without the required Task Server' >&2
  exit 1
fi

printf '%s\n' 'update-stable tests passed'
