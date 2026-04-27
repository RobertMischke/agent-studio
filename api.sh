#!/usr/bin/env bash
# Robust local .NET API control script — sh equivalent of api.ps1.
#
# Why this exists: PowerShell-based scripts behave unreliably when called
# from agent CLIs (Claude Code, Codex, Copilot CLI on Windows). Long-running
# background launches and PID tracking get mangled, agents wait for prompts
# that never come. This sh version runs cleanly under Git Bash / WSL / any
# POSIX shell available on the dev machine, and is the canonical entrypoint
# for agents.
#
# Usage:
#   ./api.sh start
#   ./api.sh stop
#   ./api.sh restart
#   ./api.sh status

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
PORT=5030
BASE_URL="http://127.0.0.1:${PORT}"
HEALTH_URL="${BASE_URL}/healthz"
PROJECT_FILE="${SCRIPT_DIR}/backend/OrchestratorApi.csproj"
PID_FILE="${SCRIPT_DIR}/.api.pid"
LOG_OUT="${SCRIPT_DIR}/.api.log.out"
LOG_ERR="${SCRIPT_DIR}/.api.log.err"

is_windows() {
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

# Returns 0 if PID is alive, 1 otherwise. Works on Git Bash & POSIX.
pid_alive() {
  local pid="$1"
  if [[ -z "$pid" ]] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then return 1; fi
  if is_windows; then
    tasklist //FI "PID eq ${pid}" 2>/dev/null | grep -qi "${pid}"
  else
    kill -0 "$pid" 2>/dev/null
  fi
}

kill_pid() {
  local pid="$1"
  if [[ -z "$pid" ]] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then return 0; fi
  if is_windows; then
    taskkill //F //T //PID "$pid" >/dev/null 2>&1 || true
  else
    kill -TERM "$pid" 2>/dev/null || true
    sleep 0.3
    kill -KILL "$pid" 2>/dev/null || true
  fi
}

# Find any PID currently listening on $PORT, regardless of pid file.
listener_pid() {
  if is_windows; then
    # netstat -ano output: "  TCP    0.0.0.0:5030  ...  LISTENING  12345"
    netstat -ano 2>/dev/null \
      | awk -v port=":${PORT}" '$2 ~ port && /LISTENING/ { print $5; exit }'
  else
    # lsof preferred, fallback to ss
    if command -v lsof >/dev/null 2>&1; then
      lsof -nP -iTCP:"${PORT}" -sTCP:LISTEN -t 2>/dev/null | head -n1
    elif command -v ss >/dev/null 2>&1; then
      ss -lntp 2>/dev/null \
        | awk -v port=":${PORT}" '$4 ~ port { match($0, /pid=[0-9]+/); if (RSTART) print substr($0, RSTART+4, RLENGTH-4); exit }'
    fi
  fi
}

# Print the API status. Sets globals: STATUS_RUNNING, STATUS_HEALTHY, STATUS_PID, STATUS_MSG.
get_status() {
  STATUS_RUNNING=0
  STATUS_HEALTHY=0
  STATUS_PID=""
  STATUS_MSG="stopped"

  if [[ -f "${PID_FILE}" ]]; then
    local stored
    stored="$(tr -d ' \r\n' < "${PID_FILE}" 2>/dev/null || true)"
    if pid_alive "${stored}"; then
      STATUS_RUNNING=1
      STATUS_PID="${stored}"
    fi
  fi

  if [[ "${STATUS_RUNNING}" -eq 0 ]]; then
    local lp
    lp="$(listener_pid)"
    if [[ -n "${lp}" ]] && pid_alive "${lp}"; then
      STATUS_RUNNING=1
      STATUS_PID="${lp}"
    fi
  fi

  if [[ "${STATUS_RUNNING}" -eq 1 ]]; then
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "${HEALTH_URL}" 2>/dev/null || echo 000)"
    if [[ "${code}" == "200" ]]; then
      STATUS_HEALTHY=1
      STATUS_MSG="running and healthy (PID: ${STATUS_PID})"
    else
      STATUS_MSG="running but unhealthy (HTTP ${code}, PID: ${STATUS_PID})"
    fi
  fi
}

cmd_status() {
  get_status
  echo "API STATUS: ${STATUS_MSG}"
}

cmd_stop() {
  get_status
  if [[ "${STATUS_RUNNING}" -eq 1 && -n "${STATUS_PID}" ]]; then
    echo "Stopping API (PID: ${STATUS_PID})..."
    kill_pid "${STATUS_PID}"
    sleep 0.5
  fi
  # Aggressive sweep: anything still listening on the port
  local lp
  lp="$(listener_pid)"
  while [[ -n "${lp}" ]]; do
    echo "Force killing lingering listener PID ${lp} on port ${PORT}..."
    kill_pid "${lp}"
    sleep 0.5
    lp="$(listener_pid)"
  done
  rm -f "${PID_FILE}"
  echo "API stopped."
}

cmd_start() {
  get_status
  if [[ "${STATUS_RUNNING}" -eq 1 ]]; then
    if [[ "${STATUS_HEALTHY}" -eq 1 ]]; then
      echo "API is already running and healthy (PID: ${STATUS_PID})."
      return 0
    fi
    echo "API is running but unhealthy. Stopping first..."
    cmd_stop
  fi

  echo "Starting API on ${BASE_URL}..."
  : > "${LOG_OUT}"
  : > "${LOG_ERR}"

  # Background-launch dotnet detached from this shell. nohup keeps it alive
  # after the script exits; the redirects keep stdout/stderr persistent.
  if is_windows; then
    # Git Bash: nohup is available; & detaches. The shell's $! is the dotnet PID.
    nohup dotnet run --project "${PROJECT_FILE}" --urls "${BASE_URL}" \
      > "${LOG_OUT}" 2> "${LOG_ERR}" &
  else
    nohup dotnet run --project "${PROJECT_FILE}" --urls "${BASE_URL}" \
      > "${LOG_OUT}" 2> "${LOG_ERR}" &
  fi
  local launched_pid=$!
  disown 2>/dev/null || true

  echo "${launched_pid}" > "${PID_FILE}"
  echo "API process started with PID: ${launched_pid}. Waiting for health check..."

  # Wait up to 30 s for the health endpoint to come up
  local attempts=0
  while (( attempts < 60 )); do
    sleep 0.5
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 1 "${HEALTH_URL}" 2>/dev/null || echo 000)"
    if [[ "${code}" == "200" ]]; then
      # Capture the actual listener PID — `dotnet run` may spawn a child that
      # owns the port, and that child is what we want to track for stops.
      local lp
      lp="$(listener_pid)"
      if [[ -n "${lp}" && "${lp}" != "${launched_pid}" ]]; then
        echo "${lp}" > "${PID_FILE}"
        echo "API listener PID: ${lp}"
      fi
      echo "API is successfully started and healthy!"
      return 0
    fi
    attempts=$((attempts + 1))
  done

  echo "ERROR: API started but did not become healthy within 30 seconds."
  echo "Check ${LOG_OUT} and ${LOG_ERR} for details."
  exit 1
}

print_usage() {
  cat <<EOF

  API Control (sh)

  Usage: ./api.sh <command>

  Commands:
    start     Start the API (skips if already healthy)
    stop      Stop the running API process
    restart   Stop + Start (full restart)
    status    Show current API status and health

EOF
}

CMD="${1:-}"
case "${CMD}" in
  start)   cmd_start ;;
  stop)    cmd_stop ;;
  restart) cmd_stop; cmd_start ;;
  status)  cmd_status ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) echo "Unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
