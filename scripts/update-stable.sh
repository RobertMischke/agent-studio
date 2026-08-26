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
#   ATP_TASK_SERVER_START_SCRIPT host-owned Task Server start/ensure wrapper
#   ATP_TASK_SERVER_READY_URL    readiness endpoint (default: http://127.0.0.1:5071/readyz)
#   ATP_TASK_SERVER_READY_SECONDS readiness deadline (default: 60)
#   ATP_STABLE_API_URL           Stable API origin used to prove the v1 proxy
#   ATP_STABLE_FRONTEND_URL      page loaded by the boot probe
#   ATP_BOOT_PROBE_SCRIPT        browser probe implementation
#   ATP_BOOT_PROBE_TIMEOUT_MS    total browser probe deadline
#   ATP_BOOT_PROBE_SETTLE_MS     time to collect deferred page errors

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
task_server_start_script=${ATP_TASK_SERVER_START_SCRIPT:-}
task_server_ready_url=${ATP_TASK_SERVER_READY_URL:-http://127.0.0.1:5071/readyz}
task_server_ready_seconds=${ATP_TASK_SERVER_READY_SECONDS:-60}
stable_api_url=${ATP_STABLE_API_URL:-http://127.0.0.1:5031}
frontend_url=${ATP_STABLE_FRONTEND_URL:-http://127.0.0.1:4011}
probe_timeout_ms=${ATP_BOOT_PROBE_TIMEOUT_MS:-180000}
probe_settle_ms=${ATP_BOOT_PROBE_SETTLE_MS:-2000}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

ensure_task_server() {
  if [ -n "$task_server_start_script" ]; then
    [ -x "$task_server_start_script" ] || fail "Task Server start script is not executable: $task_server_start_script"
    log "Starting the supervised Task Server"
    "$task_server_start_script"
  elif command -v powershell.exe >/dev/null 2>&1; then
    log "Starting the supervised Task Server scheduled task"
    powershell.exe -NoProfile -NonInteractive -Command \
      "Start-ScheduledTask -TaskName 'AgentOrchestrator-TaskServer'" >/dev/null
  else
    fail "No Task Server supervisor is available. Set ATP_TASK_SERVER_START_SCRIPT."
  fi

  started=$(date +%s)
  while ! curl -fsS --max-time 2 "$task_server_ready_url" >/dev/null 2>&1; do
    now=$(date +%s)
    if [ $((now - started)) -ge "$task_server_ready_seconds" ]; then
      fail "Task Server did not become ready at $task_server_ready_url within ${task_server_ready_seconds}s. Stable API was not started."
    fi
    sleep 1
  done
  log "Task Server authority is ready at $task_server_ready_url"
}

probe_stable_proxy() {
  started=$(date +%s)
  while ! curl -fsS --max-time 2 "$stable_api_url/api/v1/protocol" >/dev/null 2>&1; do
    now=$(date +%s)
    if [ $((now - started)) -ge "$task_server_ready_seconds" ]; then
      fail "Stable API did not proxy the Task Server protocol at $stable_api_url/api/v1/protocol within ${task_server_ready_seconds}s."
    fi
    sleep 1
  done
  log "Stable API proxy is connected to Task Server"
}

[ -e "$stable_checkout/.git" ] || fail "Stable checkout not found: $stable_checkout"
stable_checkout=$(cd "$stable_checkout" && pwd)

[ -x "$stop_script" ] || fail "Stable stop script is not executable: $stop_script"
[ -x "$start_script" ] || fail "Stable start script is not executable: $start_script"

probe_script=${ATP_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-frontend-boot-probe.mjs}

if [ -n "$(git -C "$stable_checkout" status --porcelain)" ]; then
  fail "Stable checkout has local changes; refusing to update."
fi

head_before=$(git -C "$stable_checkout" rev-parse HEAD)
branch_before=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")

if [ "$head_before" = "$target" ]; then
  if [ "$branch_before" != "$stable_branch" ]; then
    git -C "$stable_checkout" switch --quiet --force-create "$stable_branch" "$target"
    log "Attached Stable to $stable_branch at $target"
  fi
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
if [ "$branch_before" = "$stable_branch" ]; then
  git -C "$stable_checkout" merge --quiet --ff-only "$target"
else
  git -C "$stable_checkout" switch --quiet --force-create "$stable_branch" "$target"
  log "Attached Stable to $stable_branch"
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

log "Starting Stable"
ensure_task_server
DETACH=1 "$start_script"
probe_stable_proxy

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

log "Stable started and healthy at $target"
