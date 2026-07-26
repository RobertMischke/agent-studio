#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/lib.sh"

require_root
current=$(current_target) || die "No active release symlink exists."

if [ "$#" -eq 1 ]; then
    version=$(normalize_version "$1")
    target="$OPT_ROOT/$version"
else
    [ -L "$OPT_ROOT/previous" ] || die "No previous release is recorded."
    target=$(readlink -f "$OPT_ROOT/previous")
fi
[ -d "$target" ] || die "Rollback target does not exist: $target"
[ "$target" != "$current" ] || die "Rollback target is already active."
target_version=$(basename "$target")

drain_for_switch "rolling back from $(basename "$current") to $target_version"
stop_runtime
if ! switch_with_health_gate "$target" "$current"; then
    die "Rollback target $target_version was unhealthy; $(basename "$current") was restored."
fi
atomic_link "$current" "$OPT_ROOT/previous"

log "Rolled back agent-orchestrator to $target_version."
