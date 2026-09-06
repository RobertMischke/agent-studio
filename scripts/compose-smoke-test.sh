#!/usr/bin/env bash
# Compatibility entry point. The browser/API curls now live in scenario step 1.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export SCENARIO_COMPOSE_PROJECT="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
export STUDIO_UI_PORT="${COMPOSE_SMOKE_UI_PORT:-4011}"
export STUDIO_API_PORT="${COMPOSE_SMOKE_API_PORT:-5031}"
exec "$repo_root/scripts/scenario.sh" --target compose --level smoke
