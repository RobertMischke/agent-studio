#!/usr/bin/env bash
# One deployment regression entry point for host, Compose, and remote targets.
set -Eeuo pipefail

usage()
{
    cat <<'EOF'
Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full [--output DIR]

Remote target configuration:
  SCENARIO_URL       Studio BFF or Task Server base URL (required)
  SCENARIO_TOKEN     Bearer credential (required when target authentication is enabled)
  SCENARIO_RUN_ID    Optional unique fixture suffix

Exit codes: 0 passed, 1 scenario/build failure, 2 invalid invocation or missing dependency.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target=
level=
output=
while (($#)); do
    case "$1" in
        --target) (($# >= 2)) || { usage >&2; exit 2; }; target=$2; shift 2 ;;
        --level) (($# >= 2)) || { usage >&2; exit 2; }; level=$2; shift 2 ;;
        --output) (($# >= 2)) || { usage >&2; exit 2; }; output=$2; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

[[ "$target" == inproc || "$target" == compose || "$target" == remote ]] || { usage >&2; exit 2; }
[[ "$level" == smoke || "$level" == full ]] || { usage >&2; exit 2; }
if [[ -z "$output" ]]; then
    if [[ -n "${JOB_RESULTS_DIR:-}" ]]; then
        output="$JOB_RESULTS_DIR/deployment-scenario-$target-$level"
    else
        output="$repo_root/results/deployment-scenario-$target-$level"
    fi
fi
mkdir -p "$output"
output="$(cd "$output" && pwd)"

cd "$repo_root"

if [[ "$target" == inproc ]]; then
    command -v dotnet >/dev/null || { printf 'dotnet is required for --target inproc.\n' >&2; exit 2; }
    dotnet build testsupport/scenario/DeploymentScenario.csproj --configuration Release --nologo
    dotnet build task-server.Tests/TaskServer.Tests.csproj --configuration Release --nologo
    dotnet test task-server.Tests/TaskServer.Tests.csproj \
        --configuration Release \
        --no-build \
        --filter "FullyQualifiedName=TaskServer.Tests.TopologyTests.Remote_concept_run_generates_real_status_without_repeating_core_agent_run" \
        --logger "console;verbosity=minimal" \
        --nologo
    exec dotnet testsupport/scenario/bin/Release/net10.0/DeploymentScenario.dll \
        --target inproc --level "$level" --output "$output"
fi

if [[ "$target" == remote ]]; then
    command -v dotnet >/dev/null || { printf 'dotnet is required for --target remote.\n' >&2; exit 2; }
    dotnet build testsupport/scenario/DeploymentScenario.csproj --configuration Release --nologo
    [[ -n "${SCENARIO_URL:-}" ]] || { printf 'SCENARIO_URL is required for --target remote.\n' >&2; exit 2; }
    remote_args=(--target remote --level "$level" --url "$SCENARIO_URL" --output "$output")
    if [[ -n "${SCENARIO_TOKEN:-}" ]]; then remote_args+=(--token "$SCENARIO_TOKEN"); fi
    exec dotnet testsupport/scenario/bin/Release/net10.0/DeploymentScenario.dll "${remote_args[@]}"
fi

command -v docker >/dev/null || { printf 'docker is required for --target compose.\n' >&2; exit 2; }
docker compose version >/dev/null || { printf 'Docker Compose v2 is required.\n' >&2; exit 2; }
project_name="${SCENARIO_COMPOSE_PROJECT:-agent-studio-scenario}"
ui_port="${SCENARIO_UI_PORT:-4011}"
api_port="${SCENARIO_API_PORT:-5031}"
task_server_port="${SCENARIO_TASK_SERVER_PORT:-5071}"
bff_port="${SCENARIO_BFF_PORT:-5072}"
export SCENARIO_RESULTS_DIR="$output"
compose=(
    docker compose
    --file docker-compose.yml
    --file testsupport/scenario/docker-compose.scenario.yml
    --project-name "$project_name"
    --profile distributed
    --profile runner
)

cleanup()
{
    status=$?
    trap - EXIT HUP INT TERM
    if ((status != 0)); then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --no-color >"$output/compose.log" 2>&1 || true
    fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    exit "$status"
}
trap cleanup EXIT HUP INT TERM

export STUDIO_UI_PORT="$ui_port"
export STUDIO_API_PORT="$api_port"
export STUDIO_TASKSERVER_PORT="$task_server_port"
export STUDIO_BFF_PORT="$bff_port"
"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
"${compose[@]}" config --quiet
"${compose[@]}" up --build --wait orchestrator-api frontend task-server orchestrator-engine studio-bff

# The former compose-smoke-test.sh assertions are part of this definition now.
wait_for_url()
{
    url=$1
    for _ in $(seq 1 60); do
        if curl --fail --silent "$url" >/dev/null; then return 0; fi
        sleep 1
    done
    printf 'Timed out waiting for %s\n' "$url" >&2
    return 1
}
wait_for_url "http://127.0.0.1:$ui_port/healthz"
wait_for_url "http://127.0.0.1:$task_server_port/readyz"
wait_for_url "http://127.0.0.1:$bff_port/healthz"
curl --fail --silent "http://127.0.0.1:$ui_port/healthz" | grep -Fq '"ok"'
curl --fail --silent "http://127.0.0.1:$ui_port/" | grep -q '<app-root'
curl --fail --silent "http://127.0.0.1:$ui_port/api/tasks/grouped" | grep -q '"backlog"'
curl --fail --silent "http://127.0.0.1:$task_server_port/readyz" | grep -q '"ready"'
curl --fail --silent "http://127.0.0.1:$bff_port/healthz" | grep -q '"live"'

"${compose[@]}" run --rm --no-deps --build deployment-scenario \
    --target compose \
    --level "$level" \
    --url http://studio-bff:5072 \
    --output /scenario-results
