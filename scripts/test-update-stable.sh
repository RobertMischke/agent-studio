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

# A port nothing listens on, so the default rollout runs see a stopped backend.
free_port=$(node -e 'const s=require("node:net").createServer();s.listen(0,"127.0.0.1",()=>{const p=s.address().port;s.close(()=>console.log(p))})')

run_update() {
  env \
    ATP_DEVSPACE_DIR="$devspace" \
    ATP_STABLE_CHECKOUT="$stable_checkout" \
    ATP_STOP_SCRIPT="$devspace/stop-stable.sh" \
    ATP_START_SCRIPT="$devspace/start-stable.sh" \
    ATP_STABLE_BACKEND_URL="http://127.0.0.1:$free_port" \
    ATP_STABLE_STOP_TIMEOUT=2 \
    ATP_BOOT_PROBE_SCRIPT="$probe" \
    ATP_BOOT_PROBE_SETTLE_MS=0 \
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

# A stop wrapper that reports success without stopping anything must not be
# allowed to look like a rollout. Before AGT-2678 the updater fast-forwarded,
# rebuilt against a live process holding the build output, and then reported
# health from the backend that never went away.
printf '%s\n' 'release after a hollow stop' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'release after a hollow stop'
git -C "$source_checkout" push --quiet origin main

node -e '
  const net = require("node:net");
  const srv = net.createServer(() => {});
  srv.listen(Number(process.argv[1]), "127.0.0.1", () => {
    process.stdout.write("up\n");
    setTimeout(() => { srv.close(); process.exit(0); }, 60000);
  });
' "$free_port" > "$test_root/squatter.log" &
squatter_pid=$!
trap 'kill "$squatter_pid" 2>/dev/null || true; rm -rf -- "$test_root"' EXIT HUP INT TERM
for _ in 1 2 3 4 5 6 7 8 9 10; do
  grep -q up "$test_root/squatter.log" 2>/dev/null && break
  sleep 0.3
done
grep -q up "$test_root/squatter.log" || { printf '%s\n' 'fixture listener did not come up' >&2; exit 1; }

head_before_hollow=$(git -C "$stable_checkout" rev-parse HEAD)
set +e
hollow_output=$(run_update)
hollow_rc=$?
set -e

test "$hollow_rc" -ne 0
printf '%s' "$hollow_output" | grep -q 'still accepts connections'
if printf '%s' "$hollow_output" | grep -q 'Stable started and healthy'; then
  printf '%s\n' 'updater reported health after a hollow stop' >&2
  exit 1
fi
# It must refuse BEFORE touching the checkout, so nothing is rebuilt on top of
# a process that still holds the previous build output.
test "$(git -C "$stable_checkout" rev-parse HEAD)" = "$head_before_hollow"
kill "$squatter_pid" 2>/dev/null || true

printf '%s\n' 'update-stable tests passed'
