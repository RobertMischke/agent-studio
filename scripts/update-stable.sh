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
#   ATP_TASK_SERVER_REQUIRED     require/install/probe Task Server (default: 1)
#   ATP_TASK_SERVER_URL          standalone authority URL
#   ATP_STABLE_API_URL           OrchestratorApi URL used to prove proxy mode
#   ATP_TASK_SERVER_INSTALL      auto, 1 for Windows install, or 0 for external
#   ATP_POWERSHELL_BIN           Windows PowerShell executable

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
task_server_required=${ATP_TASK_SERVER_REQUIRED:-1}
task_server_url=${ATP_TASK_SERVER_URL:-http://127.0.0.1:5071}
stable_api_url=${ATP_STABLE_API_URL:-http://127.0.0.1:5031}
task_server_install=${ATP_TASK_SERVER_INSTALL:-auto}
powershell_bin=${ATP_POWERSHELL_BIN:-powershell.exe}
task_server_install_root=${ATP_TASK_SERVER_INSTALL_ROOT:-C:/AgentOrchestrator}
task_server_env_file=${ATP_TASK_SERVER_ENV_FILE:-C:/ProgramData/AgentOrchestrator/server.env}
task_server_data_dir=${ATP_TASK_SERVER_DATA_DIR:-$devspace_dir/task-server-data}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

wait_for_url() {
  url=$1
  timeout_seconds=$2
  started=$(date -u +%s)
  while ! curl -fsS --max-time 5 "$url" >/dev/null 2>&1; do
    now=$(date -u +%s)
    [ $((now - started)) -lt "$timeout_seconds" ] \
      || fail "Timed out waiting for required endpoint: $url"
    sleep 1
  done
}

[ -e "$stable_checkout/.git" ] || fail "Stable checkout not found: $stable_checkout"
stable_checkout=$(cd "$stable_checkout" && pwd)

[ -x "$stop_script" ] || fail "Stable stop script is not executable: $stop_script"
[ -x "$start_script" ] || fail "Stable start script is not executable: $start_script"

case "$task_server_required" in
  0|1) ;;
  *) fail "ATP_TASK_SERVER_REQUIRED must be 0 or 1." ;;
esac
case "$task_server_install" in
  auto|0|1) ;;
  *) fail "ATP_TASK_SERVER_INSTALL must be auto, 0, or 1." ;;
esac

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

log "Stopping Stable"
"$stop_script"

if [ "$branch_before" = "$stable_branch" ]; then
  log "Fast-forwarding Stable to $target"
  git -C "$stable_checkout" merge --quiet --ff-only "$target"
else
  if git -C "$stable_checkout" show-ref --verify --quiet "refs/heads/$stable_branch" \
    && ! git -C "$stable_checkout" merge-base --is-ancestor "refs/heads/$stable_branch" "$target"; then
    fail "Local $stable_branch cannot fast-forward to $target; refusing to replace it."
  fi
  log "Attaching Stable to $stable_branch at $target"
  git -C "$stable_checkout" switch --quiet --force-create "$stable_branch" "$target"
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

probe_arguments=(
  --frontend-dir "$stable_checkout/frontend"
  --url "$frontend_url"
  --timeout-ms "$probe_timeout_ms"
  --settle-ms "$probe_settle_ms"
)

if [ "$task_server_required" -eq 1 ]; then
  install_on_windows=$task_server_install
  if [ "$install_on_windows" = auto ]; then
    if command -v "$powershell_bin" >/dev/null 2>&1; then install_on_windows=1; else install_on_windows=0; fi
  fi
  if [ "$install_on_windows" -eq 1 ]; then
    command -v "$powershell_bin" >/dev/null 2>&1 \
      || fail "Windows Task Server install requested, but PowerShell was not found: $powershell_bin"
    task_server_installer="$stable_checkout/deploy/windows/task-server/install-task-server.ps1"
    [ -f "$task_server_installer" ] \
      || fail "Task Server installer not found: $task_server_installer"

    log "Publishing, installing, and starting Task Server before Stable API"
    "$powershell_bin" -NoProfile -NonInteractive -ExecutionPolicy Bypass \
      -File "$task_server_installer" \
      -SourceRoot "$stable_checkout" \
      -Version "$target" \
      -InstallBase "$task_server_install_root" \
      -EnvFile "$task_server_env_file" \
      -DataDirectory "$task_server_data_dir" \
      -StudioConfig "$stable_checkout/backend/appsettings.Local.json" \
      -ListenUrl "$task_server_url"
  else
    log "Using externally supervised Task Server at $task_server_url"
  fi
  wait_for_url "$task_server_url/readyz" $((probe_timeout_ms / 1000))
  probe_arguments+=(--task-server-url "$task_server_url" --api-url "$stable_api_url")
fi

log "Starting Stable"
DETACH=1 "$start_script"

log "Loading $frontend_url in a headless browser"
node "$probe_script" "${probe_arguments[@]}"

log "Stable started and healthy at $target"
