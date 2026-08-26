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
#   ATP_TASK_SERVER_REQUIRED     install, start, and probe Task Server first (default: 1)
#   ATP_TASK_SERVER_URL          standalone Task Server URL
#   ATP_TASK_SERVER_INSTALLER    Windows install script
#   ATP_TASK_SERVER_INSTALL_COMMAND executable override used by rehearsals
#   ATP_TASK_SERVER_DATA_DIR     durable data directory outside release checkout
#   ATP_TASK_SERVER_ENV_FILE     host-owned server.env path
#   ATP_TASK_SERVER_READY_TIMEOUT_SECONDS readiness deadline
#   ATP_STABLE_ATTACH_BRANCH     reattach a verified detached Stable checkout (default: 1)

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
task_server_installer=${ATP_TASK_SERVER_INSTALLER:-$stable_checkout/deploy/windows/task-server/install-task-server.ps1}
task_server_install_command=${ATP_TASK_SERVER_INSTALL_COMMAND:-powershell.exe}
task_server_data_dir=${ATP_TASK_SERVER_DATA_DIR:-$devspace_dir/task-server-data}
task_server_env_file=${ATP_TASK_SERVER_ENV_FILE:-C:/ProgramData/AgentOrchestrator/server.env}
task_server_ready_timeout=${ATP_TASK_SERVER_READY_TIMEOUT_SECONDS:-90}
attach_stable_branch=${ATP_STABLE_ATTACH_BRANCH:-1}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

wait_for_url() {
  label=$1
  url=$2
  deadline=$(( $(date -u +%s) + task_server_ready_timeout ))
  while :; do
    if curl -fsS --max-time 3 "$url" >/dev/null 2>&1; then
      log "$label ready at $url"
      return 0
    fi
    [ "$(date -u +%s)" -lt "$deadline" ] || fail "$label did not become ready at $url within ${task_server_ready_timeout}s."
    sleep 2
  done
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
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")

if [ "$head_before" = "$target" ]; then
  if [ "$attach_stable_branch" -eq 1 ] \
    && [ -z "$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD 2>/dev/null || true)" ]; then
    log "Reattaching verified Stable checkout to $stable_branch"
    git -C "$stable_checkout" branch --force "$stable_branch" "$target"
    git -C "$stable_checkout" switch --quiet "$stable_branch"
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

[ -f "$probe_script" ] || fail "Frontend boot probe not found: $probe_script"

if [ "$task_server_required" -eq 1 ]; then
  [ -f "$task_server_installer" ] || fail "Task Server installer not found: $task_server_installer"
  command -v "$task_server_install_command" >/dev/null 2>&1 \
    || fail "Task Server install command not found: $task_server_install_command"
  [ -f "$stable_checkout/backend/appsettings.Local.json" ] \
    || fail "Stable proxy configuration not found: $stable_checkout/backend/appsettings.Local.json"

  log "Publishing and supervising Task Server before Stable API startup"
  "$task_server_install_command" -NoProfile -NonInteractive -ExecutionPolicy Bypass \
    -File "$task_server_installer" \
    -SourceRoot "$stable_checkout" \
    -EnvFile "$task_server_env_file" \
    -DataDirectory "$task_server_data_dir" \
    -ListenUrl "$task_server_url"

  node "$stable_checkout/scripts/configure-task-server-proxy.mjs" \
    "$stable_checkout/backend/appsettings.Local.json" \
    "$task_server_url" \
    "${ATP_TASK_SERVER_AUTH_TOKEN_FILE:-}"
  wait_for_url "Task Server authority" "${task_server_url%/}/readyz"
fi

log "Starting Stable"
DETACH=1 "$start_script"

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

if [ "$task_server_required" -eq 1 ]; then
  wait_for_url "Stable Task Server proxy" "${ATP_STABLE_API_URL:-http://127.0.0.1:5031}/api/v1/protocol"
fi

if [ "$attach_stable_branch" -eq 1 ] \
  && [ -z "$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD 2>/dev/null || true)" ]; then
  log "Attaching verified Stable checkout to $stable_branch"
  git -C "$stable_checkout" branch --force "$stable_branch" "$target"
  git -C "$stable_checkout" switch --quiet "$stable_branch"
fi

log "Stable started and healthy at $target"
