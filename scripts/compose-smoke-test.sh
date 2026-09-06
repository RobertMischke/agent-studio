#!/usr/bin/env bash
# Compatibility entry point. The checks now live in the shared scenario runner.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export SCENARIO_COMPOSE_PROJECT="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
export SCENARIO_UI_PORT="${COMPOSE_SMOKE_UI_PORT:-4011}"
export SCENARIO_API_PORT="${COMPOSE_SMOKE_API_PORT:-5031}"
exec "$repo_root/scripts/scenario.sh" --target compose --level smoke "$@"
