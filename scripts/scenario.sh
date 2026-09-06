#!/usr/bin/env bash
# One deterministic deployment regression scenario for local, Compose, and remote targets.
set -Eeuo pipefail

usage()
{
    cat <<'EOF'
Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full

Environment for remote:
  SCENARIO_BASE_URL    Task Server or Studio BFF base URL
  SCENARIO_AUTH_TOKEN  Optional bearer token
  SCENARIO_RUN_ID      Optional stable suffix for the isolated scenario project

Output:
  SCENARIO_RESULTS_DIR, then JOB_RESULTS_DIR, otherwise results/deployment-scenario
EOF
}

target=
level=
while (($#)); do
    case "$1" in
        --target) target="${2:-}"; shift 2 ;;
        --level) level="${2:-}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$target" in inproc|compose|remote) ;; *) usage >&2; exit 2 ;; esac
case "$level" in smoke|full) ;; *) usage >&2; exit 2 ;; esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
results_dir="${SCENARIO_RESULTS_DIR:-${JOB_RESULTS_DIR:-$repo_root/results/deployment-scenario}}"
mkdir -p "$results_dir"
results_dir="$(cd "$results_dir" && pwd)"

run_host()
{
    dotnet run \
        --project "$repo_root/testsupport/scenario/DeploymentScenario.csproj" \
        --configuration Release \
        -- \
        --target "$target" \
        --level "$level" \
        --results "$results_dir"
}

if [[ "$target" != compose ]]; then
    if [[ "$target" == remote && -z "${SCENARIO_BASE_URL:-}" ]]; then
        printf 'SCENARIO_BASE_URL is required for the remote target.\n' >&2
        exit 2
    fi
    if [[ "$target" == inproc ]]; then
        set +e
        dotnet test "$repo_root/task-server.Tests/TaskServer.Tests.csproj" \
            --configuration Release \
            --filter 'FullyQualifiedName=TaskServer.Tests.TopologyTests.Remote_concept_run_generates_real_status_without_repeating_core_agent_run' \
            --logger 'console;verbosity=minimal' \
            2>&1 | tee "$results_dir/topology-harness.log"
        topology_status="${PIPESTATUS[0]}"
        set -e
        export SCENARIO_TOPOLOGY_STATUS="$topology_status"
    fi
    run_host
    exit $?
fi

command -v docker >/dev/null 2>&1 || { printf 'docker is required for the compose target.\n' >&2; exit 2; }
project_name="${SCENARIO_COMPOSE_PROJECT:-agent-studio-scenario-${GITHUB_RUN_ID:-local}}"
compose=(docker compose --project-name "$project_name" --profile distributed --profile scenario)

cleanup()
{
    status=$?
    trap - EXIT HUP INT TERM
    if ((status != 0)); then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --no-color || true
    fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

cd "$repo_root"
"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
"${compose[@]}" config --quiet
"${compose[@]}" up --build --wait orchestrator-api frontend task-server studio-bff

export SCENARIO_RESULTS_DIR="$results_dir"
"${compose[@]}" run --build --rm scenario-runner \
    --target compose \
    --level "$level" \
    --results /results
