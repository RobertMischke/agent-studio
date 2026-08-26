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
#   ATP_TASK_SERVER_START_SCRIPT supervised Task Server start/ensure helper
#   ATP_TASK_SERVER_BOOT_PROBE_SCRIPT Task Server readiness probe
#   ATP_TASK_SERVER_URL          standalone Task Server origin
#   ATP_TASK_SERVER_PROBE_TIMEOUT_MS Task Server readiness deadline

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
task_server_url=${ATP_TASK_SERVER_URL:-http://127.0.0.1:5071}
task_server_probe_timeout_ms=${ATP_TASK_SERVER_PROBE_TIMEOUT_MS:-60000}

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

probe_script=${ATP_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-frontend-boot-probe.mjs}
task_server_probe_script=${ATP_TASK_SERVER_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/task-server-boot-probe.mjs}
task_server_start_script=${ATP_TASK_SERVER_START_SCRIPT:-$stable_checkout/deploy/windows/task-server/ensure-task-server.ps1}

if [ -n "$(git -C "$stable_checkout" status --porcelain)" ]; then
  fail "Stable checkout has local changes; refusing to update."
fi

was_detached=0
if ! git -C "$stable_checkout" symbolic-ref --quiet HEAD >/dev/null; then
  was_detached=1
  log "Stable is pinned at a detached checkout; it will be reattached to $stable_branch only after all boot probes pass."
fi

head_before=$(git -C "$stable_checkout" rev-parse HEAD)
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")

update_needed=1
if [ "$head_before" = "$target" ]; then
  update_needed=0
  log "Stable already matches $stable_remote/$stable_branch; supervising the existing release."
elif ! git -C "$stable_checkout" merge-base --is-ancestor "$head_before" "$target"; then
  fail "Stable cannot fast-forward from $head_before to $target."
fi

install_frontend=0
if [ "$update_needed" -eq 1 ] && ! git -C "$stable_checkout" diff --quiet "$head_before" "$target" -- \
  frontend/package.json \
  frontend/package-lock.json \
  'frontend/scripts/patch-coding-agent-chat-*.mjs'; then
  install_frontend=1
fi

if [ "$update_needed" -eq 1 ]; then
  log "Stopping Stable"
  "$stop_script"

  log "Fast-forwarding Stable to $target"
  git -C "$stable_checkout" merge --quiet --ff-only "$target"
fi

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

[ -f "$probe_script" ] || fail "Frontend boot probe not found: $probe_script"
[ -f "$task_server_probe_script" ] || fail "Task Server boot probe not found: $task_server_probe_script"

log "Starting supervised Task Server before Stable API"
case "$task_server_start_script" in
  *.ps1)
    command -v powershell.exe >/dev/null 2>&1 \
      || fail "powershell.exe is required to start the Windows Task Server service."
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass \
      -File "$task_server_start_script"
    ;;
  *)
    [ -x "$task_server_start_script" ] \
      || fail "Task Server start script is not executable: $task_server_start_script"
    "$task_server_start_script"
    ;;
esac

log "Waiting for Task Server authority at $task_server_url"
case "$task_server_probe_script" in
  *.mjs|*.js)
    node "$task_server_probe_script" \
      --url "$task_server_url" \
      --timeout-ms "$task_server_probe_timeout_ms"
    ;;
  *)
    [ -x "$task_server_probe_script" ] \
      || fail "Task Server boot probe is not executable: $task_server_probe_script"
    "$task_server_probe_script" "$task_server_url" "$task_server_probe_timeout_ms"
    ;;
esac

if [ "$update_needed" -eq 1 ]; then
  log "Starting Stable"
  DETACH=1 "$start_script"
else
  log "Stable source is unchanged; leaving the running API process in place"
fi

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

if [ "$was_detached" -eq 1 ]; then
  log "Boot probes passed; reattaching Stable to $stable_branch"
  git -C "$stable_checkout" switch --quiet -C "$stable_branch" --track "$stable_remote/$stable_branch"
fi

log "Stable started and healthy at $target"
