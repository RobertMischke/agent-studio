#!/usr/bin/env bash
# Hermetic acceptance check for the default new-user Docker Compose path.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
ui_port="${COMPOSE_SMOKE_UI_PORT:-4011}"
api_port="${COMPOSE_SMOKE_API_PORT:-5031}"
compose=(docker compose --project-name "$project_name")

down()
{
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}

finish()
{
    status="$1"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --no-color || true
    fi
    down
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

cd "$repo_root"
down

export STUDIO_UI_PORT="$ui_port"
export STUDIO_API_PORT="$api_port"

"${compose[@]}" config --quiet

services="$("${compose[@]}" config --services)"
test "$services" = "$(printf 'orchestrator-api\nfrontend')"

"${compose[@]}" up --build --wait

ui_binding="$("${compose[@]}" port frontend 80)"
api_binding="$("${compose[@]}" port orchestrator-api 5031)"
resolved_ui_port="${ui_binding##*:}"
resolved_api_port="${api_binding##*:}"

health="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/healthz")"
test "$health" = '"ok"'

homepage="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/")"
grep -q '<app-root' <<<"$homepage"

tasks="$(curl --fail --silent "http://127.0.0.1:${resolved_ui_port}/api/tasks/grouped")"
grep -q '"backlog"' <<<"$tasks"

status="$("${compose[@]}" ps --format json)"
test "$(grep -o '"Health":"healthy"' <<<"$status" | wc -l)" -eq 2

printf '%s\n' \
    "compose-smoke=passed" \
    "project=$project_name" \
    "services=orchestrator-api,frontend" \
    "health=$health" \
    "browser-shell=app-root" \
    "api-tasks-grouped=json" \
    "ui-port=$resolved_ui_port" \
    "api-port=$resolved_api_port"
