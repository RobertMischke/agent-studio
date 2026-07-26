#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/lib.sh"

require_root
[ "$#" -eq 1 ] || die "Usage: update.sh <vX.Y.Z|release-directory>"
[ -f "$CONFIG_ROOT/server.env" ] || die "No installation found in $CONFIG_ROOT; run install.sh first."

old_target=$(current_target) || die "No active release symlink exists."
resolve_release_source "$1" "$SCRIPT_DIR"
trap cleanup_resolved_source EXIT HUP INT TERM
new_target=$(install_release_tree "$RESOLVED_SOURCE")

if [ "$new_target" = "$old_target" ]; then
    log "Version $(basename "$new_target") is already active; no update is needed."
    exit 0
fi

drain_for_switch "updating from $(basename "$old_target") to $(basename "$new_target")"
stop_runtime || abort_after_stop_failure
if ! switch_with_health_gate "$new_target" "$old_target"; then
    die "Candidate $(basename "$new_target") was rolled back because /readyz stayed red."
fi
atomic_link "$old_target" "$OPT_ROOT/previous"

log "Updated agent-orchestrator to $(basename "$new_target"); previous points to $(basename "$old_target")."
