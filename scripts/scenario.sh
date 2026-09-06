#!/usr/bin/env bash
# One entry point for the deterministic deployment regression scenario.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target=""
level=""
base_url="${SCENARIO_BASE_URL:-}"
auth_token="${SCENARIO_AUTH_TOKEN:-}"
output_dir="${JOB_RESULTS_DIR:-$repo_root/scenario-results}"

usage()
{
    printf '%s\n' \
        'Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full' \
        '       [--url URL] [--token TOKEN] [--output DIRECTORY]'
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --target) target="${2:-}"; shift 2 ;;
        --level) level="${2:-}"; shift 2 ;;
        --url) base_url="${2:-}"; shift 2 ;;
        --token) auth_token="${2:-}"; shift 2 ;;
        --output) output_dir="${2:-}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$target" in inproc|compose|remote) ;; *) usage >&2; exit 2 ;; esac
case "$level" in smoke|full) ;; *) usage >&2; exit 2 ;; esac

output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd)"
run_output="$output_dir/deployment-scenario-$target-$level"
mkdir -p "$run_output"

run_driver()
{
    args=(
        --target "$target"
        --level "$level"
        --definition "$repo_root/testsupport/scenario/definition.json"
        --output "$run_output"
    )
    if [ -n "$base_url" ]; then
        args+=(--url "$base_url")
    fi
    SCENARIO_AUTH_TOKEN="$auth_token" dotnet run --project "$repo_root/testsupport/scenario/ScenarioRunner.csproj" \
        --configuration Release -- "${args[@]}"
}

if [ "$target" = inproc ]; then
    run_driver
    exit $?
fi

if [ "$target" = remote ]; then
    if [ -z "$base_url" ]; then
        printf '%s\n' 'remote target requires --url or SCENARIO_BASE_URL' >&2
        exit 2
    fi
    run_driver
    exit $?
fi

project_name="${COMPOSE_SCENARIO_PROJECT:-agent-studio-deployment-scenario}"
compose=(docker compose --project-name "$project_name" --profile distributed --profile runner)
compose_fixture_dir="$(mktemp -d)"
export RUNNER_ENV_FILE="$compose_fixture_dir/runner.env"
export RUNNER_TOKEN_FILE="$compose_fixture_dir/runner.token"
printf '%s\n' 'RUNNER_ID=scenario-unused' > "$RUNNER_ENV_FILE"
printf '%s\n' 'scenario-unused' > "$RUNNER_TOKEN_FILE"

compose_down()
{
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}

finish()
{
    status="$1"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" ps > "$run_output/compose-ps.txt" 2>&1 || true
        "${compose[@]}" logs --no-color > "$run_output/compose.log" 2>&1 || true
    fi
    compose_down
    rm -f "$RUNNER_ENV_FILE" "$RUNNER_TOKEN_FILE"
    rmdir "$compose_fixture_dir" 2>/dev/null || true
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

cd "$repo_root"
compose_down
export SCENARIO_RESULTS_DIR="$run_output"
export SCENARIO_LEVEL="$level"
"${compose[@]}" config --quiet
"${compose[@]}" up --build --wait task-server studio-bff
"${compose[@]}" run --build --rm deployment-scenario-runner
