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
#   ATP_TASK_SERVER_REQUIRED     supervise Task Server (default: 1 on Windows)
#   ATP_TASK_SERVER_URL          direct Task Server origin
#   ATP_STABLE_API_URL           Stable API origin used by topology probes
#   ATP_TASK_SERVER_INSTALL_SCRIPT
#                                Windows package/install implementation
#   ATP_TASK_SERVER_CONTROL_SCRIPT
#                                supervised start/stop implementation
#   ATP_POWERSHELL               Windows PowerShell executable

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ -d "$script_dir/../frontend" ] && [ -d "$script_dir/../.git" ]; then
  source_checkout=$(cd "$script_dir/.." && pwd)
  default_devspace=$(cd "$source_checkout/.." && pwd)
else
  source_checkout=
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
stable_api_url=${ATP_STABLE_API_URL:-http://127.0.0.1:5031}
task_server_url=${ATP_TASK_SERVER_URL:-http://127.0.0.1:5071}
powershell_executable=${ATP_POWERSHELL:-powershell.exe}

case "$(uname -s 2>/dev/null || printf unknown)" in
  MINGW*|MSYS*|CYGWIN*) default_task_server_required=1 ;;
  *) default_task_server_required=0 ;;
esac
task_server_required=${ATP_TASK_SERVER_REQUIRED:-$default_task_server_required}
asset_checkout=${source_checkout:-$stable_checkout}
task_server_install_script=${ATP_TASK_SERVER_INSTALL_SCRIPT:-$asset_checkout/deploy/windows/task-server/install-task-server.ps1}
task_server_control_script=${ATP_TASK_SERVER_CONTROL_SCRIPT:-$asset_checkout/deploy/windows/task-server/task-server-control.ps1}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

case "$task_server_required" in
  0|1) ;;
  *) fail "ATP_TASK_SERVER_REQUIRED must be 0 or 1." ;;
esac

windows_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s\n' "$1"
  fi
}

run_task_server_asset() {
  asset=$1
  shift
  case "$asset" in
    *.ps1)
      "$powershell_executable" -NoProfile -NonInteractive -ExecutionPolicy Bypass \
        -File "$(windows_path "$asset")" "$@"
      ;;
    *) "$asset" "$@" ;;
  esac
}

task_server_protocol() {
  curl -fsS --max-time 10 "$task_server_url/api/v1/protocol" \
    | sed -n 's/.*"current"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
    | head -n 1
}

task_server_request() {
  method=$1
  path=$2
  body=$3
  protocol=$(task_server_protocol)
  [ -n "$protocol" ] || fail "Task Server protocol could not be read from $task_server_url."
  curl -fsS --max-time 15 \
    -X "$method" \
    -H "X-Task-Protocol-Version: $protocol" \
    -H 'X-Client-Id: stable-updater' \
    -H 'X-Actor-Id: stable-updater' \
    -H 'Content-Type: application/json' \
    --data "$body" \
    "$task_server_url$path"
}

prepare_task_server_shutdown() {
  log "Draining Task Server before package replacement"
  task_server_request PUT /api/v1/management/mode \
    '{"mode":"draining","reason":"planned Stable release update"}' >/dev/null
  prepared=$(task_server_request POST /api/v1/management/prepare-shutdown \
    '{"reason":"planned Stable release update"}')
  printf '%s' "$prepared" | grep -qE '"safeToStop"[[:space:]]*:[[:space:]]*true' \
    || fail "Task Server still has active or process-unknown authority; it remains Draining and Stable was not stopped."
}

wait_for_http() {
  name=$1
  url=$2
  shift 2
  deadline=$(( $(date -u +%s) + (probe_timeout_ms / 1000) + 1 ))
  while :; do
    if curl -fsS --max-time 10 "$@" "$url" >/dev/null 2>&1; then
      return 0
    fi
    [ "$(date -u +%s)" -lt "$deadline" ] \
      || fail "Timed out waiting for $name at $url."
    sleep 1
  done
}

probe_stable_topology() {
  protocol=$(task_server_protocol)
  [ -n "$protocol" ] || fail "Task Server protocol could not be read after restart."
  wait_for_http "Task Server readiness" "$task_server_url/readyz"
  wait_for_http "Stable API health" "$stable_api_url/healthz"
  wait_for_http "Stable Task Server proxy" "$stable_api_url/api/v1/protocol"
  wait_for_http "Stable board projection" "$stable_api_url/api/tasks/grouped" \
    -H 'X-Client-Id: stable-updater'
  wait_for_http "Stable Task Server management proxy" "$stable_api_url/api/v1/management/status" \
    -H "X-Task-Protocol-Version: $protocol" \
    -H 'X-Client-Id: stable-updater'
}

[ -e "$stable_checkout/.git" ] || fail "Stable checkout not found: $stable_checkout"
stable_checkout=$(cd "$stable_checkout" && pwd)

[ -x "$stop_script" ] || fail "Stable stop script is not executable: $stop_script"
[ -x "$start_script" ] || fail "Stable start script is not executable: $start_script"
if [ "$task_server_required" -eq 1 ]; then
  [ -f "$task_server_install_script" ] || fail "Task Server install script not found: $task_server_install_script"
  [ -f "$task_server_control_script" ] || fail "Task Server control script not found: $task_server_control_script"
fi

probe_script=${ATP_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-frontend-boot-probe.mjs}

if [ -n "$(git -C "$stable_checkout" status --porcelain)" ]; then
  fail "Stable checkout has local changes; refusing to update."
fi

head_before=$(git -C "$stable_checkout" rev-parse HEAD)
branch_before=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")

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

if [ "$task_server_required" -eq 1 ]; then
  prepare_task_server_shutdown
fi
log "Stopping Stable"
"$stop_script"

if [ "$task_server_required" -eq 1 ]; then
  log "Stopping supervised Task Server"
  run_task_server_asset "$task_server_control_script" \
    -Action Stop \
    -ReadyUrl "$task_server_url/readyz" \
    -AllowMissing
fi

log "Deploying Stable candidate $target in detached verification state"
git -C "$stable_checkout" switch --quiet --detach "$target"

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

if [ "$task_server_required" -eq 1 ]; then
  log "Packaging and installing Task Server before Stable API startup"
  run_task_server_asset "$task_server_install_script" \
    -SourceCheckout "$(windows_path "$stable_checkout")" \
    -DevspacePath "$(windows_path "$devspace_dir")" \
    -ListenUrl "$task_server_url" \
    -NoStart
  log "Starting supervised Task Server"
  run_task_server_asset "$task_server_control_script" \
    -Action Start \
    -ReadyUrl "$task_server_url/readyz"
fi

log "Starting Stable"
DETACH=1 "$start_script"

if [ "$task_server_required" -eq 1 ]; then
  log "Probing Task Server, Stable proxy, API, and board projection"
  probe_stable_topology
fi

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

if [ "$task_server_required" -eq 1 ]; then
  task_server_request PUT /api/v1/management/mode \
    '{"mode":"normal","reason":"Stable topology verification passed"}' >/dev/null
fi

log "Attaching verified Stable checkout to $stable_branch"
git -C "$stable_checkout" switch --quiet -C "$stable_branch" "$stable_remote/$stable_branch"
git -C "$stable_checkout" branch --set-upstream-to="$stable_remote/$stable_branch" "$stable_branch" >/dev/null

log "Stable started and healthy at $target"
