#!/usr/bin/env bash

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

usage() {
  demo_error "Usage: rollback-demo-runtime.sh --runtime-root <absolute-dir> --start-hook <executable> --probe-hook <executable> --switch-hook <executable> [--stop-hook <executable>]"
  exit 2
}

runtime_root= start_hook= probe_hook= switch_hook= stop_hook=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtime-root) runtime_root="${2:-}"; shift 2 ;;
    --start-hook) start_hook="${2:-}"; shift 2 ;;
    --probe-hook) probe_hook="${2:-}"; shift 2 ;;
    --switch-hook) switch_hook="${2:-}"; shift 2 ;;
    --stop-hook) stop_hook="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done
[[ -n "$runtime_root" && -n "$start_hook" && -n "$probe_hook" && -n "$switch_hook" ]] || usage
runtime_root="$(readlink -m -- "$runtime_root")" || exit 1
demo_validate_runtime_root "$runtime_root" || exit 1
demo_require_hook "$start_hook" "start" || exit 1
demo_require_hook "$probe_hook" "probe" || exit 1
demo_require_hook "$switch_hook" "switch" || exit 1
[[ -z "$stop_hook" ]] || demo_require_hook "$stop_hook" "stop" || exit 1

current="$(demo_link_target "$runtime_root/current")"
previous="$(demo_link_target "$runtime_root/previous")"
[[ -n "$current" && -n "$previous" ]] || { demo_error "both current and previous releases are required for rollback"; exit 1; }
verifier="${DEMO_RELEASE_VERIFIER:-$REPO_ROOT/scripts/demo-release/verify-demo-release.mjs}"
demo_require_file "$verifier" "release verifier" || exit 1
demo_acquire_lock "$runtime_root" || exit 1
trap demo_release_lock EXIT HUP INT TERM

node "$verifier" --directory "$previous/release" --require-approved >/dev/null || { demo_error "previous pristine release no longer verifies"; exit 1; }
candidate="$(mktemp -d "$runtime_root/releases/.rollback.XXXXXX")" || exit 1
activated=0
cleanup() {
  local exit_code=$?
  if [[ $activated -eq 0 && -d "$candidate" ]]; then
    chmod -R u+w "$candidate" 2>/dev/null || true
    rm -rf -- "$candidate"
  fi
  demo_release_lock
  exit "$exit_code"
}
trap cleanup EXIT HUP INT TERM
mkdir "$candidate/release" || exit 1
cp -a "$previous/release/." "$candidate/release/" || exit 1
cp -a "$candidate/release/runtime" "$candidate/runtime" || exit 1
chmod -R a-w "$candidate/release" || exit 1
release_version="$(node -e 'const fs=require("node:fs"); const path=require("node:path"); const manifest=JSON.parse(fs.readFileSync(path.join(process.argv[1], "release", "demo-release-manifest.json"), "utf8")); process.stdout.write(manifest.demoRelease);' "$candidate")" || exit 1
versioned_candidate="$(mktemp -d "$runtime_root/releases/${release_version}.rollback.XXXXXX")" || exit 1
rmdir "$versioned_candidate" || exit 1
mv "$candidate" "$versioned_candidate" || exit 1
candidate="$versioned_candidate"
"$start_hook" "$candidate" "$current" || { demo_error "rollback target failed to start"; exit 1; }
"$probe_hook" "$candidate" "$current" || {
  demo_error "rollback target failed its probes"
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
}
"$switch_hook" "$candidate" "$current" || {
  demo_error "rollback traffic switch failed; current runtime remains active"
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
}
if ! demo_atomic_link "$current" "$runtime_root/previous"; then
  demo_error "previous-release link update failed after rollback switch; restoring current traffic"
  "$switch_hook" "$current" "$candidate" || true
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
fi
if ! demo_atomic_link "$candidate" "$runtime_root/current"; then
  demo_error "current-release link update failed after rollback switch; restoring current traffic"
  "$switch_hook" "$current" "$candidate" || true
  demo_atomic_link "$previous" "$runtime_root/previous" || true
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
fi
activated=1
[[ -z "$stop_hook" ]] || "$stop_hook" "$current" "$candidate" || demo_error "former runtime stop hook failed after rollback"
demo_prune_inactive_releases "$runtime_root" || demo_error "inactive runtime cleanup failed after rollback"
demo_log "rollback activated a fresh $(basename "$candidate"); replaced release retained as previous"
