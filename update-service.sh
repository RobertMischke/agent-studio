#!/usr/bin/env bash
# update-service.sh — start / stop / restart / status for the standalone
# UpdateService process (port 5039). Mirrors api.sh's shape.
#
# Why a sibling script (and not api.sh): the UpdateService is the *one*
# .NET process that must NOT die when the main backend dies. Treat it as
# infrastructure that lives next to the main backend, not part of it.
#
# Default port can be overridden via PORT env var.
#
# Usage:
#   ./update-service.sh start
#   ./update-service.sh stop
#   ./update-service.sh restart
#   ./update-service.sh status

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}/update-service"
PORT="${PORT:-5039}"
PID_FILE="${SCRIPT_DIR}/.update-service.pid"
LOG_FILE="${SCRIPT_DIR}/.update-service.log"

listener_pid() {
  netstat -ano 2>/dev/null \
    | awk -v port=":${PORT} " '
        $0 ~ "LISTENING" && index($0, port) > 0 { print $NF; exit }
      '
}

cmd_start() {
  local existing
  existing="$(listener_pid)"
  if [ -n "${existing}" ]; then
    echo "UpdateService already listening on :${PORT} (PID: ${existing})."
    return 0
  fi

  echo "Starting UpdateService on http://127.0.0.1:${PORT} ..."
  : > "${LOG_FILE}"
  (
    cd "${PROJECT_DIR}" && \
    nohup dotnet run --no-launch-profile --urls "http://127.0.0.1:${PORT}" \
      > "${LOG_FILE}" 2>&1 &
    echo $! > "${PID_FILE}"
  )

  # Wait up to 60 s for /healthz=200.
  for _ in $(seq 1 60); do
    sleep 1
    if curl -fsS --max-time 2 "http://127.0.0.1:${PORT}/healthz" >/dev/null 2>&1; then
      echo "UpdateService is healthy on :${PORT}."
      return 0
    fi
  done
  echo "WARN: UpdateService did not become healthy within 60s. Tail log: ${LOG_FILE}" >&2
  return 1
}

cmd_stop() {
  local pid
  pid="$(listener_pid)"
  if [ -z "${pid}" ] && [ -r "${PID_FILE}" ]; then
    pid="$(cat "${PID_FILE}")"
  fi
  if [ -z "${pid}" ]; then
    echo "UpdateService is not running on :${PORT}."
    rm -f "${PID_FILE}" 2>/dev/null
    return 0
  fi
  echo "Stopping UpdateService (PID: ${pid})..."
  taskkill //F //PID "${pid}" >/dev/null 2>&1 || kill -9 "${pid}" 2>/dev/null || true
  sleep 1
  if [ -n "$(listener_pid)" ]; then
    echo "WARN: port :${PORT} still occupied" >&2
    return 1
  fi
  rm -f "${PID_FILE}"
  echo "UpdateService stopped; port :${PORT} is free."
}

cmd_status() {
  local pid
  pid="$(listener_pid)"
  if [ -n "${pid}" ]; then
    echo "UpdateService listening on :${PORT} (PID: ${pid})."
    if curl -fsS --max-time 2 "http://127.0.0.1:${PORT}/update/status" 2>/dev/null; then echo; fi
  else
    echo "UpdateService is not running on :${PORT}."
  fi
}

case "${1:-}" in
  start)   cmd_start ;;
  stop)    cmd_stop ;;
  restart) cmd_stop; cmd_start ;;
  status)  cmd_status ;;
  *)       echo "usage: $0 {start|stop|restart|status}"; exit 2 ;;
esac
