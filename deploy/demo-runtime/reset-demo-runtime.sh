#!/usr/bin/env bash

# Replaces the complete public-demo runtime from one approved release bundle.
# The candidate is verified, started, probed, and switched before the former
# release is retained as the rollback target. No live datastore is rewritten.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

usage() {
  demo_error "Usage: reset-demo-runtime.sh --bundle <tar.gz> --bundle-digest sha256:<digest> --runtime-root <absolute-dir> --start-hook <executable> --probe-hook <executable> --switch-hook <executable> [--stop-hook <executable>]"
  exit 2
}

bundle= bundle_digest= runtime_root= start_hook= probe_hook= switch_hook= stop_hook=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --bundle) bundle="${2:-}"; shift 2 ;;
    --bundle-digest) bundle_digest="${2:-}"; shift 2 ;;
    --runtime-root) runtime_root="${2:-}"; shift 2 ;;
    --start-hook) start_hook="${2:-}"; shift 2 ;;
    --probe-hook) probe_hook="${2:-}"; shift 2 ;;
    --switch-hook) switch_hook="${2:-}"; shift 2 ;;
    --stop-hook) stop_hook="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$bundle" && "$bundle_digest" =~ ^sha256:[a-f0-9]{64}$ && -n "$runtime_root" && -n "$start_hook" && -n "$probe_hook" && -n "$switch_hook" ]] || usage
runtime_root="$(readlink -m -- "$runtime_root")" || exit 1
demo_require_file "$bundle" "release bundle" || exit 1
demo_validate_runtime_root "$runtime_root" || exit 1
demo_require_hook "$start_hook" "start" || exit 1
demo_require_hook "$probe_hook" "probe" || exit 1
demo_require_hook "$switch_hook" "switch" || exit 1
[[ -z "$stop_hook" ]] || demo_require_hook "$stop_hook" "stop" || exit 1

verifier="${DEMO_RELEASE_VERIFIER:-$REPO_ROOT/scripts/demo-release/verify-demo-release.mjs}"
demo_require_file "$verifier" "release verifier" || exit 1
mkdir -p "$runtime_root/releases"
demo_acquire_lock "$runtime_root" || exit 1

candidate="$(mktemp -d "$runtime_root/releases/.candidate.XXXXXX")" || { demo_release_lock; exit 1; }
mkdir "$candidate/release" || { rm -rf -- "$candidate"; demo_release_lock; exit 1; }
current="$(demo_link_target "$runtime_root/current")"
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

demo_log "verifying approved bundle and extracting isolated candidate"
if ! node "$verifier" --bundle "$bundle" --extract-to "$candidate/release" --expected-bundle-digest "$bundle_digest" --require-approved >/dev/null; then
  demo_error "bundle verification failed; current runtime remains unchanged"
  exit 1
fi
release_version="$(node -e 'const fs=require("node:fs"); const path=require("node:path"); const manifest=JSON.parse(fs.readFileSync(path.join(process.argv[1], "release", "demo-release-manifest.json"), "utf8")); process.stdout.write(manifest.demoRelease);' "$candidate")" || exit 1
cp -a "$candidate/release/runtime" "$candidate/runtime" || exit 1
chmod -R a-w "$candidate/release" || exit 1
versioned_candidate="$(mktemp -d "$runtime_root/releases/${release_version}.XXXXXX")" || exit 1
rmdir "$versioned_candidate" || exit 1
mv "$candidate" "$versioned_candidate" || exit 1
candidate="$versioned_candidate"

demo_log "starting candidate $candidate"
if ! "$start_hook" "$candidate" "$current"; then
  demo_error "candidate start failed; current runtime remains unchanged"
  exit 1
fi
if ! "$probe_hook" "$candidate" "$current"; then
  demo_error "candidate browse and denial probes failed; current runtime remains unchanged"
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
fi
if ! "$switch_hook" "$candidate" "$current"; then
  demo_error "atomic traffic switch failed; current runtime remains unchanged"
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
fi

if [[ -n "$current" ]]; then
  if ! demo_atomic_link "$current" "$runtime_root/previous"; then
    demo_error "previous-release link update failed after switch; attempting traffic rollback"
    "$switch_hook" "$current" "$candidate" || true
    [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
    exit 1
  fi
fi
if ! demo_atomic_link "$candidate" "$runtime_root/current"; then
  demo_error "runtime link update failed after switch; attempting traffic rollback"
  "$switch_hook" "$current" "$candidate" || true
  [[ -z "$stop_hook" ]] || "$stop_hook" "$candidate" "$current" || true
  exit 1
fi
activated=1
if [[ -n "$current" && -n "$stop_hook" ]]; then
  "$stop_hook" "$current" "$candidate" || demo_error "former runtime stop hook failed after successful cut"
fi
demo_prune_inactive_releases "$runtime_root" || demo_error "inactive runtime cleanup failed after successful cut"
demo_log "activated complete release $(basename "$candidate"); previous healthy release retained"
