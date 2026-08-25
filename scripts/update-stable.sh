#!/usr/bin/env bash
# Canonical updater for the side-by-side Agent Studio Stable checkout.
#
# The script can run from this repository or be copied unchanged to the
# devspace root. It keeps Stable on a fast-forward-only main line, reinstalls
# frontend dependencies when their inputs change, and only reports a healthy
# start after a real browser has loaded the frontend without a page error.
#
# Environment overrides:
#   ATP_DEVSPACE_DIR             parent of the dev and stable checkouts
#   ATP_STABLE_CHECKOUT          Stable checkout to update
#   ATP_STABLE_REMOTE            Git remote to fetch (default: origin)
#   ATP_STABLE_BRANCH            remote branch to deploy (default: main)
#   ATP_STOP_SCRIPT              external Stable stop wrapper
#   ATP_START_SCRIPT             external Stable start wrapper
#   ATP_STABLE_FRONTEND_URL      page loaded by the boot probe
#   ATP_BOOT_PROBE_SCRIPT        browser probe implementation
#   ATP_BOOT_PROBE_TIMEOUT_MS    total browser probe deadline
#   ATP_BOOT_PROBE_SETTLE_MS     time to collect deferred page errors
#   ATP_TASK_SERVER_ENABLED      require Task Server package/start/probe (default: 1)
#   ATP_TASK_SERVER_ENSURE       override the Windows ensure script
#   ATP_TASK_SERVER_URL          standalone Task Server origin
#   ATP_STABLE_API_URL           Stable OrchestratorApi origin

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ -d "$script_dir/../frontend" ] && [ -d "$script_dir/../.git" ]; then
  source_checkout=$(cd "$script_dir/.." && pwd)
  default_devspace=$(cd "$source_checkout/.." && pwd)
else
  default_devspace=$script_dir
fi

devspace_dir=${ATP_DEVSPACE_DIR:-$default_devspace}
stable_checkout=${ATP_STABLE_CHECKOUT:-$devspace_dir/agent-taskboard-stable}
stable_remote=${ATP_STABLE_REMOTE:-origin}
stable_branch=${ATP_STABLE_BRANCH:-main}
stop_script=${ATP_STOP_SCRIPT:-$devspace_dir/stop-stable.sh}
start_script=${ATP_START_SCRIPT:-$devspace_dir/start-stable.sh}
frontend_url=${ATP_STABLE_FRONTEND_URL:-http://127.0.0.1:4011}
probe_timeout_ms=${ATP_BOOT_PROBE_TIMEOUT_MS:-180000}
probe_settle_ms=${ATP_BOOT_PROBE_SETTLE_MS:-2000}
task_server_enabled=${ATP_TASK_SERVER_ENABLED:-1}
task_server_url=${ATP_TASK_SERVER_URL:-http://127.0.0.1:5071}
stable_api_url=${ATP_STABLE_API_URL:-http://127.0.0.1:5031}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

[ -e "$stable_checkout/.git" ] || fail "Stable checkout not found: $stable_checkout"
stable_checkout=$(cd "$stable_checkout" && pwd)

[ -x "$stop_script" ] || fail "Stable stop script is not executable: $stop_script"
[ -x "$start_script" ] || fail "Stable start script is not executable: $start_script"

case "$task_server_enabled" in
  0|1) ;;
  *) fail "ATP_TASK_SERVER_ENABLED must be 0 or 1." ;;
esac

probe_script=${ATP_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-frontend-boot-probe.mjs}

if [ -n "$(git -C "$stable_checkout" status --porcelain)" ]; then
  fail "Stable checkout has local changes; refusing to update."
fi

head_before=$(git -C "$stable_checkout" rev-parse HEAD)
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")
branch_before=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)

if [ "$head_before" = "$target" ] && [ "$branch_before" = "$stable_branch" ]; then
  log "Stable already matches $stable_remote/$stable_branch; nothing to update."
  exit 0
fi

if ! git -C "$stable_checkout" merge-base --is-ancestor "$head_before" "$target"; then
  fail "Stable cannot fast-forward from $head_before to $target."
fi

install_frontend=0
if ! git -C "$stable_checkout" diff --quiet "$head_before" "$target" -- \
  frontend/package.json \
  frontend/package-lock.json \
  'frontend/scripts/patch-coding-agent-chat-*.mjs'; then
  install_frontend=1
fi

log "Stopping Stable"
"$stop_script"

log "Fast-forwarding Stable to $target"
git -C "$stable_checkout" merge --quiet --ff-only "$target"

if [ "$install_frontend" -eq 1 ]; then
  log "Installing frontend dependencies"
  npm --prefix "$stable_checkout/frontend" install

  # postinstall patches coding-agent-chat in place. Angular's Vite optimizer
  # does not include those resulting bytes in its cache key, so any prebundle
  # created before the patch is unsafe even when the package version is equal.
  angular_cache="$stable_checkout/frontend/.angular/cache"
  case "$angular_cache" in
    "$stable_checkout"/frontend/.angular/cache)
      rm -rf -- "$angular_cache"
      ;;
    *)
      fail "Refusing to remove unexpected Angular cache path: $angular_cache"
      ;;
  esac
  log "Invalidated the Angular/Vite optimizer cache after npm install"
else
  log "Frontend dependency inputs are unchanged; skipping npm install"
fi

if [ "$task_server_enabled" -eq 1 ]; then
  task_server_ensure=${ATP_TASK_SERVER_ENSURE:-$stable_checkout/deploy/windows/task-server/ensure-task-server.ps1}
  [ -f "$task_server_ensure" ] || fail "Task Server ensure script not found: $task_server_ensure"
  command -v powershell.exe >/dev/null 2>&1 || fail "powershell.exe is required to supervise the Windows Task Server."
  ps_path() {
    if command -v cygpath >/dev/null 2>&1; then
      cygpath -w "$1"
    else
      printf '%s\n' "$1"
    fi
  }
  log "Packaging, installing, and probing Task Server before Stable API start"
  powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass \
    -File "$(ps_path "$task_server_ensure")" \
    -SourceRoot "$(ps_path "$stable_checkout")" \
    -DevspaceRoot "$(ps_path "$devspace_dir")" \
    -ReleaseId "$target" \
    -ListenUrl "$task_server_url"
fi

[ -f "$probe_script" ] || fail "Frontend boot probe not found: $probe_script"

log "Starting Stable"
DETACH=1 "$start_script"

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

if [ "$task_server_enabled" -eq 1 ]; then
  task_server_probe=${ATP_TASK_SERVER_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-task-server-boot-probe.mjs}
  [ -f "$task_server_probe" ] || fail "Task Server boot probe not found: $task_server_probe"
  log "Verifying standalone authority through the Stable proxy"
  node "$task_server_probe" "$stable_api_url" "$task_server_url"
fi

current_branch=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
if [ "$current_branch" != "$stable_branch" ]; then
  git -C "$stable_checkout" switch --quiet -C "$stable_branch" "$target"
  log "Reattached Stable to $stable_branch after successful verification"
fi

log "Stable started and healthy at $target"
