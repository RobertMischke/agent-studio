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
#   ATP_TASK_SERVER_START_SCRIPT optional supervised Task Server start wrapper
#   ATP_TASK_SERVER_URL          standalone Task Server origin (read from
#                                backend/appsettings.Local.json when omitted)
#   ATP_STABLE_BACKEND_URL       API origin used for the proxy boot probe
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
frontend_url=${ATP_STABLE_FRONTEND_URL:-http://127.0.0.1:4011}
backend_url=${ATP_STABLE_BACKEND_URL:-http://127.0.0.1:5031}
probe_timeout_ms=${ATP_BOOT_PROBE_TIMEOUT_MS:-180000}
probe_settle_ms=${ATP_BOOT_PROBE_SETTLE_MS:-2000}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

read_task_server_url() {
  local settings=$stable_checkout/backend/appsettings.Local.json
  [ -f "$settings" ] || return 0
  node -e '
    const fs = require("node:fs");
    const path = process.argv[1];
    const value = JSON.parse(fs.readFileSync(path, "utf8").replace(/^\uFEFF/, "")).TaskServer?.BaseUrl;
    if (typeof value === "string") process.stdout.write(value.replace(/\/$/, ""));
  ' "$settings"
}

start_task_server() {
  if [ -n "${ATP_TASK_SERVER_START_SCRIPT:-}" ]; then
    [ -x "$ATP_TASK_SERVER_START_SCRIPT" ] \
      || fail "Task Server start script is not executable: $ATP_TASK_SERVER_START_SCRIPT"
    "$ATP_TASK_SERVER_START_SCRIPT"
  elif command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoProfile -NonInteractive -Command \
      "Start-ScheduledTask -TaskName 'AgentOrchestrator-TaskServer'"
  elif command -v systemctl >/dev/null 2>&1; then
    systemctl start agent-task-server.service
  else
    fail "TaskServer:BaseUrl is configured, but no supervised Task Server start mechanism is available."
  fi
}

probe_http() {
  local label=$1 url=$2 deadline=$(( $(date +%s) + 60 ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    if curl -fsS --max-time 3 "$url" >/dev/null 2>&1; then
      log "$label ready: $url"
      return 0
    fi
    sleep 1
  done
  fail "$label did not become ready: $url"
}

attach_stable_branch() {
  local current_branch
  current_branch=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD 2>/dev/null || true)
  if [ "$current_branch" != "$stable_branch" ]; then
    log "Attaching Stable to $stable_branch at $target"
    git -C "$stable_checkout" switch --quiet --force-create "$stable_branch" "$target"
  fi
  git -C "$stable_checkout" branch --set-upstream-to="$stable_remote/$stable_branch" "$stable_branch" >/dev/null 2>&1 || true
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
  attach_stable_branch
  log "Stable already matches $stable_remote/$stable_branch and is attached to $stable_branch; nothing to update."
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

task_server_url=${ATP_TASK_SERVER_URL:-$(read_task_server_url)}
if [ -n "$task_server_url" ]; then
  log "Starting supervised Task Server before Stable API"
  start_task_server
  probe_http "Task Server" "$task_server_url/readyz"
fi

log "Starting Stable"
DETACH=1 "$start_script"

if [ -n "$task_server_url" ]; then
  probe_http "Stable Task Server proxy" "$backend_url/api/v1/protocol"
fi

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

attach_stable_branch
log "Stable started and healthy at $target"
