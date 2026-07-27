#!/usr/bin/env bash
# Agent Studio installer - container-default.
#
# Fresh installs run the containerised stack (docker compose). The legacy
# systemd path stays available as an explicit opt-out for hosts that cannot
# run containers.
#
#   ./deploy/installer/install.sh                 # container stack (default)
#   ./deploy/installer/install.sh --with-runner   # + coding/review agent-hosts
#   ./deploy/installer/install.sh --systemd       # opt-out: legacy systemd path
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MODE="container"
WITH_RUNNER=0

for arg in "$@"; do
  case "$arg" in
    --systemd) MODE="systemd" ;;
    --with-runner) WITH_RUNNER=1 ;;
    -h|--help)
      sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [[ "$MODE" == "systemd" ]]; then
  echo "[installer] systemd opt-out selected."
  echo "[installer] task-server/engine: deploy/release/agent-orchestrator/install.sh"
  echo "[installer] runner host:        scripts/remote-runner-onboard.sh"
  exit 0
fi

command -v docker >/dev/null 2>&1 || {
  echo "[installer] docker is required for the container-default install." >&2
  echo "[installer] Install docker (e.g. 'apt-get install docker.io docker-compose-v2')" >&2
  echo "[installer] or re-run with --systemd for the legacy path." >&2
  exit 3
}
docker compose version >/dev/null 2>&1 || {
  echo "[installer] docker compose v2 is required (docker-compose-v2 package)." >&2
  exit 3
}

cd "$REPO_ROOT"

PROFILES=()
if [[ "$WITH_RUNNER" == "1" ]]; then
  if [[ ! -f runner.env ]]; then
    cp deploy/release/agent-host/runner.env.template runner.env
    echo "[installer] Wrote ./runner.env from the template."
    echo "[installer] Fill in RUNNER_ID/RUNNER_NAME/RUNNER_AUTH_TOKEN_FILE/RUNNER_GIT_* first,"
    echo "[installer] then re-run: $0 --with-runner"
    exit 4
  fi
  PROFILES+=(--profile runner)
fi

echo "[installer] Building images (first build downloads base images - takes a while)..."
docker compose "${PROFILES[@]}" build

echo "[installer] Starting the stack..."
docker compose "${PROFILES[@]}" up -d

echo
echo "[installer] Agent Studio is starting:"
echo "[installer]   Studio UI:  http://localhost:4011"
echo "[installer]   API:        http://127.0.0.1:5031/api"
if [[ "$WITH_RUNNER" == "1" ]]; then
  echo "[installer]   Runners:    agent-host-coding + agent-host-review (docker compose ps)"
else
  echo "[installer]   Runners:    none yet - re-run with --with-runner once runner.env is filled in."
fi
echo "[installer] Logs: docker compose logs -f"
