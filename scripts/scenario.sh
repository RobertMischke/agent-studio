#!/usr/bin/env bash
# One deterministic deployment regression scenario for local, Compose, and
# already-deployed Task Server targets.
set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/scenario.sh --target inproc|compose|remote --level smoke|full [options]

Options:
  --url URL             Required for the remote target.
  --token-file PATH     Bearer token file for an authenticated remote target.
  --results-dir PATH    Report directory (default: JOB_RESULTS_DIR or ./results).
  --repeat N            Require N consecutive passing runs (default: 1).

Exit codes: 0 passed, 1 scenario assertion failed, 2 invalid configuration,
3 target startup or readiness failed.
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target=
level=
remote_url=
token_file=
results_dir="${JOB_RESULTS_DIR:-$repo_root/results}"
repeat=1

while (($#)); do
  case "$1" in
    --target) (($# >= 2)) || { usage >&2; exit 2; }; target=$2; shift 2 ;;
    --level) (($# >= 2)) || { usage >&2; exit 2; }; level=$2; shift 2 ;;
    --url) (($# >= 2)) || { usage >&2; exit 2; }; remote_url=$2; shift 2 ;;
    --token-file) (($# >= 2)) || { usage >&2; exit 2; }; token_file=$2; shift 2 ;;
    --results-dir) (($# >= 2)) || { usage >&2; exit 2; }; results_dir=$2; shift 2 ;;
    --repeat) (($# >= 2)) || { usage >&2; exit 2; }; repeat=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ "$target" == inproc || "$target" == compose || "$target" == remote ]] || {
  printf 'Invalid or missing --target.\n' >&2
  exit 2
}
[[ "$level" == smoke || "$level" == full ]] || {
  printf 'Invalid or missing --level.\n' >&2
  exit 2
}
[[ "$repeat" =~ ^[1-9][0-9]*$ ]] || {
  printf -- '--repeat must be a positive integer.\n' >&2
  exit 2
}
if [[ "$target" == remote && -z "$remote_url" ]]; then
  printf -- '--url is required for the remote target.\n' >&2
  exit 2
fi
if [[ -n "$token_file" ]]; then
  [[ -f "$token_file" ]] || { printf 'Token file does not exist: %s\n' "$token_file" >&2; exit 2; }
  export SCENARIO_BEARER_TOKEN
  SCENARIO_BEARER_TOKEN="$(tr -d '\r\n' < "$token_file")"
fi

mkdir -p "$results_dir"
results_dir="$(cd "$results_dir" && pwd)"
run_root="$(mktemp -d "${TMPDIR:-/tmp}/agent-studio-scenario.XXXXXX")"
task_server_pid=
studio_pid=
compose_project="agent-studio-scenario-$$"
compose=(docker compose --project-name "$compose_project" --profile distributed --profile runner)

stop_pid() {
  local pid=${1:-}
  [[ -n "$pid" ]] || return 0
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
}

cleanup() {
  local status=$?
  trap - EXIT HUP INT TERM
  stop_pid "$studio_pid"
  stop_pid "$task_server_pid"
  if [[ "$target" == compose ]]; then
    if ((status != 0)); then
      "${compose[@]}" ps || true
      "${compose[@]}" logs --no-color > "$results_dir/deployment-scenario-compose.log" 2>&1 || true
    fi
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  rm -rf "$run_root"
  exit "$status"
}
trap cleanup EXIT HUP INT TERM

wait_ready() {
  local url=$1
  local log_file=$2
  local attempt
  for attempt in $(seq 1 90); do
    if curl --fail --silent --show-error "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  printf 'Target did not become ready: %s\n' "$url" >&2
  [[ -f "$log_file" ]] && tail -200 "$log_file" >&2
  return 3
}

cd "$repo_root"
case "$target" in
  inproc)
    if [[ "${SCENARIO_SKIP_BUILD:-0}" != 1 ]]; then
      dotnet build task-server/TaskServer.csproj --configuration Release --nologo
      dotnet build studio-bff/StudioBff.csproj --configuration Release --nologo
    fi
    port_base=$((52000 + ($$ % 800)))
    task_server_url="http://127.0.0.1:$port_base"
    studio_url="http://127.0.0.1:$((port_base + 1000))"
    dotnet task-server/bin/Release/net10.0/task-server.dll \
      --urls "$task_server_url" \
      --TaskServer:DataDirectory "$run_root/store" \
      --TaskServer:BackupDirectory "$run_root/backups" \
      > "$results_dir/deployment-scenario-task-server.log" 2>&1 &
    task_server_pid=$!
    wait_ready "$task_server_url/readyz" "$results_dir/deployment-scenario-task-server.log" || exit 3
    dotnet studio-bff/bin/Release/net10.0/agent-studio-bff.dll \
      --urls "$studio_url" --TaskServer:BaseUrl "$task_server_url" \
      > "$results_dir/deployment-scenario-studio-bff.log" 2>&1 &
    studio_pid=$!
    wait_ready "$studio_url/healthz" "$results_dir/deployment-scenario-studio-bff.log" || exit 3
    scenario_url=$studio_url
    ;;
  compose)
    command -v docker >/dev/null 2>&1 || { printf 'Docker is required for the compose target.\n' >&2; exit 3; }
    port_base=$((53000 + ($$ % 800)))
    export STUDIO_UI_PORT=$port_base
    export STUDIO_API_PORT=$((port_base + 1000))
    export STUDIO_TASKSERVER_PORT=$((port_base + 2000))
    export STUDIO_BFF_PORT=$((port_base + 3000))
    export TASK_SERVER_AUTH_TOKEN="compose-scenario-token-0000000000000001"
    export RUNNER_DOCKERFILE=testsupport/scenario/Dockerfile
    export RUNNER_ENV_FILE="$run_root/runner.env"
    export RUNNER_TOKEN_FILE="$run_root/runner.token"
    export RUNNER_SERVER_URL=http://task-server:5071
    export RUNNER_CODING_ID=scenario-compose-coding
    export RUNNER_CODING_NAME=scenario-compose-coding
    export RUNNER_CODING_HOSTNAME=scenario-compose-host
    export RUNNER_REVIEW_ID=scenario-compose-review
    export RUNNER_REVIEW_NAME=scenario-compose-review
    export RUNNER_REVIEW_HOSTNAME=scenario-compose-host
    printf '%s\n' \
      'RUNNER_GIT_REMOTE=/var/lib/agent-host/scenario-origin.git' \
      'RUNNER_GIT_PUSH_REMOTE=/var/lib/agent-host/scenario-origin.git' \
      'RUNNER_MAX_PARALLELISM=1' \
      'RUNNER_POLL_SECONDS=1' \
      > "$RUNNER_ENV_FILE"
    printf '%s\n' "$TASK_SERVER_AUTH_TOKEN" > "$RUNNER_TOKEN_FILE"
    chmod 600 "$RUNNER_ENV_FILE" "$RUNNER_TOKEN_FILE"
    "${compose[@]}" config --quiet || exit 3
    "${compose[@]}" up --build --wait \
      orchestrator-api frontend task-server studio-bff \
      agent-host-coding agent-host-review || exit 3
    health="$(curl --fail --silent "http://127.0.0.1:${STUDIO_UI_PORT}/healthz")"
    [[ "$health" == '"ok"' ]] || { printf 'Compose frontend health contract failed.\n' >&2; exit 3; }
    curl --fail --silent "http://127.0.0.1:${STUDIO_UI_PORT}/" | grep -q '<app-root' || exit 3
    curl --fail --silent "http://127.0.0.1:${STUDIO_UI_PORT}/api/tasks/grouped" | grep -q '"backlog"' || exit 3
    scenario_url="http://127.0.0.1:${STUDIO_BFF_PORT}"
    export SCENARIO_BEARER_TOKEN=$TASK_SERVER_AUTH_TOKEN
    wait_ready "$scenario_url/api/v1/protocol" "$results_dir/deployment-scenario-compose-readiness.log" || exit 3
    ;;
  remote)
    scenario_url=${remote_url%/}
    wait_ready "$scenario_url/healthz" "$results_dir/deployment-scenario-remote-readiness.log" || {
      # A Task Server may be supplied directly; /api/v1/protocol is authoritative.
      wait_ready "$scenario_url/api/v1/protocol" "$results_dir/deployment-scenario-remote-readiness.log" || exit 3
    }
    ;;
esac

publish_stable_report() {
  local run_index=$1
  local extension
  for extension in md json junit.xml; do
    cp "$results_dir/deployment-scenario-$target-$level-$run_index.$extension" \
      "$results_dir/deployment-scenario-$target-$level.$extension"
  done
}

for run_index in $(seq 1 "$repeat"); do
  printf '[scenario] target=%s level=%s consecutive-run=%s/%s\n' "$target" "$level" "$run_index" "$repeat"
  if SCENARIO_TARGET=$target \
    SCENARIO_LEVEL=$level \
    SCENARIO_RUN_INDEX=$run_index \
    SCENARIO_URL=$scenario_url \
    SCENARIO_RESULTS_DIR=$results_dir \
      node scripts/scenario-runner.mjs; then
    publish_stable_report "$run_index"
  else
    scenario_status=$?
    publish_stable_report "$run_index"
    exit "$scenario_status"
  fi
done

printf 'DEPLOYMENT_SCENARIO=passed target=%s level=%s consecutive-runs=%s report=%s\n' \
  "$target" "$level" "$repeat" "$results_dir/deployment-scenario-$target-$level.md"
