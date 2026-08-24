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
#   ATP_STABLE_BACKEND_URL       backend checked for a real stop and a real restart
#   ATP_STABLE_STOP_TIMEOUT      seconds to wait for the backend port to close
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
stop_timeout=${ATP_STABLE_STOP_TIMEOUT:-20}
probe_timeout_ms=${ATP_BOOT_PROBE_TIMEOUT_MS:-180000}
probe_settle_ms=${ATP_BOOT_PROBE_SETTLE_MS:-2000}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

# The external stop and start wrappers report on their own say-so. A wrapper
# whose stop was hollow leaves the old backend serving, and every later step
# still looks green: git fast-forwards, the browser probe loads a page that the
# OLD process rendered, and the rollout is reported as healthy while it shipped
# nothing. The two helpers below turn "Stable was replaced" into an observation
# (AGT-2678).
backend_port_open() {
  local hostport host port
  hostport=${backend_url#*://}
  hostport=${hostport%%/*}
  host=${hostport%%:*}
  port=${hostport##*:}
  [ "$port" != "$hostport" ] || port=80
  (exec 3<>"/dev/tcp/$host/$port") 2>/dev/null
}

# Process identity of whatever answers /healthz right now, empty when nothing
# does. Published by backend/Host/SystemEndpoints.cs.
backend_identity() {
  curl -s -o /dev/null -D - --noproxy '*' --max-time 5 "$backend_url/healthz" 2>/dev/null \
    | tr -d '\r' \
    | awk 'tolower($1) == "x-agent-studio-process-id:" || tolower($1) == "x-agent-studio-process-start:" { print $1 $2 }' \
    | tr '\n' ' ' || true
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

identity_before=$(backend_identity)

log "Stopping Stable"
"$stop_script"

waited=0
while [ "$waited" -lt "$stop_timeout" ] && backend_port_open; do
  sleep 1
  waited=$((waited + 1))
done
if backend_port_open; then
  fail "Stop wrapper $stop_script returned, but $backend_url still accepts connections after ${stop_timeout}s.
       Refusing to fast-forward and rebuild onto a live backend: the old process would keep
       serving the old code, and its open handles block the rebuild from replacing the build
       output. Stop it where it was started, then re-run. Background:
       docs/operations/setup/troubleshooting.md, 'api.sh restarted successfully but the old
       process is still serving'."
fi

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

log "Starting Stable"
DETACH=1 "$start_script"

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

identity_after=$(backend_identity)
if [ -n "$identity_before" ] && [ "$identity_before" = "$identity_after" ]; then
  fail "Stable is still served by the same process as before the update ($identity_after).
       The stop did not take effect, so this rollout shipped nothing even though every
       other step reported success. Treat $target as NOT deployed."
fi

log "Stable started and healthy at $target"
