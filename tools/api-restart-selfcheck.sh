#!/usr/bin/env bash
# Executable proof that api.sh's lifecycle contract holds (AGT-2678).
#
# Why this exists: on 2026-08-23 `api.sh restart` in agent-taskboard-stable
# printed "API stopped." and then "API is successfully started and healthy!"
# while the OrchestratorApi process from 12:34 kept serving the port through
# two restarts. Rollouts silently served old code, project-settings.json
# patches never took effect, and the watchdog's restart path was hollow.
# A restart that cannot prove it replaced the process is worth nothing, so
# the proof is kept executable rather than written down once.
#
# Two layers:
#
#   unit    Drives api.sh's own functions and its CLI against stub processes
#           on dynamic ports. No .NET build, no backend, a few seconds. Covers
#           the guards that turn a silent failure into a loud one.
#   e2e     Boots the REAL backend on a dynamic free port against an isolated
#           temp workspace (the ASS-1715 worktree-test-backend path, so it can
#           never become a second pickup driver), restarts it, and proves the
#           answering process is a different one. Needs dotnet.
#
# Usage:
#   bash tools/api-restart-selfcheck.sh            # unit + e2e
#   bash tools/api-restart-selfcheck.sh --quick    # unit only
#
# Exit code 0 means every check passed. Skips are reported and do not fail the
# run, but they are printed so nobody mistakes a skipped proof for a passed one.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
API_SH="${REPO_ROOT}/api.sh"

RUN_E2E=1
for arg in "$@"; do
  case "${arg}" in
    --quick) RUN_E2E=0 ;;
    -h|--help)
      sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown argument: ${arg}" >&2; exit 2 ;;
  esac
done

pass=0; fail=0; skip=0
ok()   { echo "  ok:   $*"; pass=$((pass + 1)); }
bad()  { echo "  FAIL: $*"; fail=$((fail + 1)); }
warn() { echo "  skip: $*"; skip=$((skip + 1)); }
section() { echo; echo "== $* =="; }

# --------------------------------------------------------------------------
# Fixtures
# --------------------------------------------------------------------------

STUB_PIDS=""
TMP_DIRS=""

cleanup() {
  local pid dir
  for pid in ${STUB_PIDS}; do
    pkill -KILL -P "${pid}" 2>/dev/null || true
    kill -KILL "${pid}" 2>/dev/null || true
  done
  for dir in ${TMP_DIRS}; do
    case "${dir}" in
      */atp-api-selfcheck-*) rm -rf "${dir}" ;;
    esac
  done
}
trap cleanup EXIT INT TERM

free_port() {
  if command -v node >/dev/null 2>&1 && [[ -f "${REPO_ROOT}/scripts/find-free-port.mjs" ]]; then
    node "${REPO_ROOT}/scripts/find-free-port.mjs" --count 1 2>/dev/null | awk '{ print $1 }'
    return 0
  fi
  python3 - <<'PY' 2>/dev/null
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
}

# A listener that is NOT this checkout's backend: no Agent Studio headers, no
# checkout path in its command line. Prints the pid of the process that owns
# the port.
start_stub_listener() {
  local port="$1"
  command -v python3 >/dev/null 2>&1 || return 1
  ( cd /tmp && exec python3 -m http.server "${port}" --bind 127.0.0.1 >/dev/null 2>&1 ) &
  local pid=$!
  STUB_PIDS="${STUB_PIDS} ${pid}"
  local waited=0
  while (( waited < 50 )); do
    if curl -s -o /dev/null --noproxy '*' --max-time 1 "http://127.0.0.1:${port}/" 2>/dev/null; then
      printf '%s' "${pid}"
      return 0
    fi
    kill -0 "${pid}" 2>/dev/null || return 1
    sleep 0.1
    waited=$((waited + 1))
  done
  return 1
}

port_is_open() {
  local port="$1"
  (exec 3<>"/dev/tcp/127.0.0.1/${port}") 2>/dev/null
}

# Source api.sh as a library on a given port, then run a snippet against its
# functions. Runs in a subshell so nothing leaks between checks.
with_api_lib() {
  local port="$1"; shift
  (
    export PORT="${port}" API_PORT_OVERRIDE=1 ATP_API_SH_LIB=1
    # shellcheck source=/dev/null
    . "${API_SH}"
    refresh_process_table
    eval "$*"
  )
}

# --------------------------------------------------------------------------
# Unit layer
# --------------------------------------------------------------------------

check_port_match_is_anchored() {
  section "port match is anchored"
  # ":5030" as a substring also matches ":50300". A stop that resolves the
  # wrong pid this way either kills a stranger or reports a phantom owner.
  local wide narrow
  wide="$(free_port)"
  narrow="${wide%?}"
  if [[ -z "${wide}" || ! "${narrow}" =~ ^[0-9]+$ || "${narrow}" -lt 1024 ]]; then
    warn "could not allocate a port whose number is a prefix of another"
    return 0
  fi
  if port_is_open "${narrow}"; then
    warn "prefix port ${narrow} is already in use; cannot run the anchoring check"
    return 0
  fi
  local stub
  stub="$(start_stub_listener "${wide}")" || { warn "no python3 stub listener available"; return 0; }

  local seen_wide seen_narrow
  seen_wide="$(with_api_lib "${wide}" 'listener_pids')"
  seen_narrow="$(with_api_lib "${narrow}" 'listener_pids')"
  if [[ "${seen_wide}" == *"${stub}"* ]] || [[ -n "${seen_wide}" ]]; then
    ok "listener on :${wide} is discovered (pids: ${seen_wide//$'\n'/ })"
  else
    bad "listener on :${wide} was not discovered at all"
  fi
  if [[ -z "${seen_narrow}" ]]; then
    ok "a listener on :${wide} is NOT reported as owning :${narrow}"
  else
    bad "port match leaked across ports: :${narrow} reported pids ${seen_narrow//$'\n'/ }"
  fi
  kill -KILL "${stub}" 2>/dev/null || true
}

check_kill_tree_takes_children() {
  section "kill_tree takes the whole process tree"
  # `dotnet run` execs a child that owns the port and the build output DLLs.
  # Killing only the parent is what left the zombie holding DLLs so the
  # rebuild could not copy.
  local port; port="$(free_port)"
  local parent child
  ( exec bash -c 'sleep 300 & sleep 300' ) &
  parent=$!
  STUB_PIDS="${STUB_PIDS} ${parent}"
  sleep 0.5
  child="$(pgrep -P "${parent}" 2>/dev/null | head -n1)"
  if [[ -z "${child}" ]]; then
    warn "could not build a parent/child fixture (pgrep unavailable?)"
    return 0
  fi

  local found
  found="$(with_api_lib "${port}" "descendants_of ${parent}")"
  if [[ "${found}" == *"${child}"* ]]; then
    ok "descendants_of(${parent}) finds child ${child}"
  else
    bad "descendants_of(${parent}) missed child ${child} (got: ${found//$'\n'/ })"
  fi

  with_api_lib "${port}" "kill_tree ${parent}" >/dev/null 2>&1
  sleep 0.5
  if kill -0 "${child}" 2>/dev/null; then
    bad "child ${child} survived kill_tree of parent ${parent}"
  else
    ok "child ${child} died with the tree"
  fi
  if kill -0 "${parent}" 2>/dev/null; then
    bad "parent ${parent} survived kill_tree"
  else
    ok "parent ${parent} died"
  fi
}

check_pid_zero_is_never_a_kill_target() {
  section "pid 0 and 1 are never kill targets"
  # `kill -TERM 0` signals the caller's whole process group: this script, the
  # shell around it, and the session that started it. Both 0 and 1 can reach
  # the kill path for real (netstat -ano reports 0 as the owner of some
  # listening sockets; a truncated .api.pid reads back as 0).
  local port; port="$(free_port)"
  local out
  out="$(with_api_lib "${port}" '
      for p in 0 1 2 " " x; do
        if sane_pid "$p"; then echo "accept:[$p]"; else echo "reject:[$p]"; fi
      done')"
  if printf '%s' "${out}" | grep -q 'reject:\[0\]' && printf '%s' "${out}" | grep -q 'reject:\[1\]'; then
    ok "sane_pid rejects 0 and 1"
  else
    bad "sane_pid accepted a process-group-wide pid: ${out//$'\n'/ }"
  fi
  if printf '%s' "${out}" | grep -q 'accept:\[2\]'; then
    ok "sane_pid still accepts a real pid"
  else
    bad "sane_pid rejects valid pids: ${out//$'\n'/ }"
  fi

  # A pid file containing 0 must not become a target.
  out="$(with_api_lib "${port}" '
      printf "0\n" > "${PID_FILE}"
      refresh_process_table
      echo "pid_file_target=[$(pid_file_target)]"
      echo "stop_targets=[$(stop_targets | tr "\n" " ")]"
      rm -f "${PID_FILE}"')"
  if printf '%s' "${out}" | grep -q 'pid_file_target=\[\]'; then
    ok "a .api.pid containing 0 yields no kill target"
  else
    bad "pid 0 from the pid file became a kill target: ${out//$'\n'/ }"
  fi
  if printf '%s' "${out}" | grep -qE 'stop_targets=\[ *\]'; then
    ok "stop_targets is empty rather than containing 0"
  else
    bad "stop_targets leaked a dangerous pid: ${out//$'\n'/ }"
  fi
}

check_process_table_is_refreshed() {
  section "the process table is re-read on every sweep"
  # The Windows fallback populated PROC_TABLE only when it was empty, so from
  # the second sweep on, every decision used a snapshot taken before the kills
  # (stop could never confirm) and before the launch (start would report its
  # own healthy backend as a hollow restart).
  local port; port="$(free_port)"
  local state
  state="$(mktemp -d "${TMPDIR:-/tmp}/atp-api-selfcheck-XXXXXX")" || { bad "mktemp failed"; return 0; }
  TMP_DIRS="${TMP_DIRS} ${state}"
  printf '0\n' > "${state}/calls"

  # Force the Windows code path, with no powershell.exe and a `ps` stub whose
  # output changes between calls.
  local out
  out="$(with_api_lib "${port}" "
      is_windows() { return 0; }
      ps() {
        local n; n=\$(cat '${state}/calls'); n=\$((n + 1)); echo \$n > '${state}/calls'
        echo '      PID    PPID    PGID    WINPID  TTY  UID  STIME COMMAND'
        echo \"     100     1     100     90\${n}  ?  1000 10:00 /sweep\${n}/OrchestratorApi\"
      }
      refresh_process_table; first=\"\$(proc_command 901)\$(proc_command 902)\"
      refresh_process_table; second=\"\$(proc_command 901)\$(proc_command 902)\"
      echo \"first=[\$first] second=[\$second]\"")"
  if printf '%s' "${out}" | grep -q 'first=\[/sweep1/OrchestratorApi\]' \
     && printf '%s' "${out}" | grep -q 'second=\[/sweep2/OrchestratorApi\]'; then
    ok "each sweep sees the current process table"
  else
    bad "the process table went stale after the first sweep: ${out//$'\n'/ }"
  fi
}

check_project_scoping_is_anchored() {
  section "project scoping does not leak across checkouts or ports"
  # project_pids decides what stop is allowed to kill. Substring matching made
  # "/checkout" match "/checkout-worktree", and ":5030" match ":50301" - which
  # is inside the ephemeral range worktree test backends are allocated from.
  local port=5030
  local out
  out="$(with_api_lib "${port}" '
      SCRIPT_DIR="/dev/checkout"; BASE_URL="http://127.0.0.1:5030"
      PROC_TABLE="11\t1\tdotnet run --project /dev/checkout/backend/OrchestratorApi.csproj --urls http://127.0.0.1:5030
12\t11\t/dev/checkout/backend/bin/Debug/net10.0/OrchestratorApi --urls http://127.0.0.1:5030
13\t1\tdotnet run --project /dev/checkout-worktree/backend/OrchestratorApi.csproj --urls http://127.0.0.1:5030
14\t1\t/dev/checkout/backend/bin/Debug/net10.0/OrchestratorApi --urls http://127.0.0.1:50301
15\t1\tdotnet run --project /dev/checkout/frontend/Other.csproj --urls http://127.0.0.1:5030"
      PROC_TABLE="$(printf "%b" "${PROC_TABLE}")"
      echo "matched=[$(project_pids | tr "\n" " ")]"')"
  if printf '%s' "${out}" | grep -qE 'matched=\[11 12 *\]'; then
    ok "matches only this checkout's backend on this port (${out#*matched=})"
  else
    bad "scoping leaked; expected [11 12], got ${out#*matched=}"
  fi
}

check_stop_fails_loudly_when_kill_does_not_work() {
  section "stop reports failure when it cannot free the port"
  # The exact 2026-08-23 shape: the kill does not take effect (a Windows
  # backend cannot be killed from WSL, another user's process cannot be
  # signalled) and the port keeps answering. The old script printed
  # "API stopped." anyway. It must now exit non-zero.
  local port; port="$(free_port)"
  local stub
  stub="$(start_stub_listener "${port}")" || { warn "no python3 stub listener available"; return 0; }

  local out rc
  out="$(with_api_lib "${port}" 'ATP_API_STOP_TIMEOUT=1; kill_tree() { :; }; cmd_stop' 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    ok "cmd_stop exits ${rc} when the port stays occupied"
  else
    bad "cmd_stop reported success while :${port} was still served"
  fi
  if printf '%s' "${out}" | grep -qi "could not free port"; then
    ok "cmd_stop names the failure"
  else
    bad "cmd_stop failure message missing; output: ${out}"
  fi
  if printf '%s' "${out}" | grep -qi "^API stopped"; then
    bad "cmd_stop still printed the success line"
  else
    ok "cmd_stop does not print a success line"
  fi

  # And restart must not go on to "start" anything after that failed stop.
  out="$(with_api_lib "${port}" 'ATP_API_STOP_TIMEOUT=1; kill_tree() { :; }; cmd_start() { echo "START WAS CALLED"; }; cmd_restart' 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]] && ! printf '%s' "${out}" | grep -q "START WAS CALLED"; then
    ok "cmd_restart aborts without starting when stop fails"
  else
    bad "cmd_restart continued past a failed stop (rc=${rc}); output: ${out}"
  fi

  kill -KILL "${stub}" 2>/dev/null || true
}

check_stop_is_honest_when_discovery_is_blind() {
  section "stop stays honest when no pid discovery tool works"
  # The closest reproduction of the 2026-08-23 incident: every pid-discovery
  # source comes back empty, so the sweep has nothing to kill, while the port
  # keeps answering. The old script printed "API stopped." and then started
  # "successfully" on top of the process that never died. The port probe is
  # the ground truth that makes that impossible now.
  local port; port="$(free_port)"
  local stub
  stub="$(start_stub_listener "${port}")" || { warn "no python3 stub listener available"; return 0; }

  local blind
  blind="$(mktemp -d "${TMPDIR:-/tmp}/atp-api-selfcheck-XXXXXX")" || { bad "mktemp failed"; return 0; }
  TMP_DIRS="${TMP_DIRS} ${blind}"
  local tool
  for tool in lsof ss netstat fuser; do
    printf '#!/bin/sh\nexit 1\n' > "${blind}/${tool}"
    chmod +x "${blind}/${tool}"
  done

  local out rc
  out="$(cd "${REPO_ROOT}" && env PATH="${blind}:${PATH}" PORT="${port}" API_PORT_OVERRIDE=1 \
          ATP_API_STOP_TIMEOUT=2 bash "${API_SH}" stop 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    ok "stop exits ${rc} when it cannot see or kill the owner"
  else
    bad "stop reported success while :${port} was still served and no owner was visible"
  fi
  if printf '%s' "${out}" | grep -qi "no owning PID"; then
    ok "stop explains the pid-visibility boundary"
  else
    bad "stop did not explain why it failed; output: ${out}"
  fi

  # And restart must not paper over it with a fresh start.
  out="$(cd "${REPO_ROOT}" && env PATH="${blind}:${PATH}" PORT="${port}" API_PORT_OVERRIDE=1 \
          ATP_ALLOW_DEV_BACKEND=1 ATP_API_STOP_TIMEOUT=2 bash "${API_SH}" restart 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]] && ! printf '%s' "${out}" | grep -qi "successfully started and healthy"; then
    ok "restart exits ${rc} instead of claiming a start it did not perform"
  else
    bad "restart claimed success over a process it never stopped; rc=${rc}, output: ${out}"
  fi
  if kill -0 "${stub}" 2>/dev/null; then
    ok "the untouched process is still there, and the script said so"
  else
    bad "the stub died unexpectedly; the check did not test what it claims"
  fi

  kill -KILL "${stub}" 2>/dev/null || true
}

check_start_refuses_foreign_owner() {
  section "start refuses a port owned by a foreign process"
  local port; port="$(free_port)"
  local stub
  stub="$(start_stub_listener "${port}")" || { warn "no python3 stub listener available"; return 0; }

  local out rc
  out="$(cd "${REPO_ROOT}" && env PORT="${port}" API_PORT_OVERRIDE=1 ATP_ALLOW_DEV_BACKEND=1 \
          bash "${API_SH}" start 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    ok "api.sh start exits ${rc} instead of booting onto an occupied port"
  else
    bad "api.sh start reported success while a foreign process owned :${port}"
  fi
  if printf '%s' "${out}" | grep -qi "already owned by a process that is not this checkout"; then
    ok "start names the foreign owner"
  else
    bad "start did not report a foreign owner; output: ${out}"
  fi
  if printf '%s' "${out}" | grep -qi "successfully started and healthy"; then
    bad "start claimed success while the foreign process was serving"
  else
    ok "start never claims the foreign process as its own"
  fi
  if kill -0 "${stub}" 2>/dev/null; then
    ok "start left the foreign process alone (it kills nothing it does not own)"
  else
    bad "start killed a process it does not own (PID ${stub})"
  fi

  kill -KILL "${stub}" 2>/dev/null || true
}

check_start_rejects_a_port_it_did_not_take() {
  section "start rejects a health 200 that comes from another process"
  # This is the guard that would have caught the incident: the new dotnet run
  # dies with "address already in use", the OLD process answers /healthz, and
  # the health poll passes. Ownership is asserted against the launched pid.
  local port; port="$(free_port)"
  local stub
  stub="$(start_stub_listener "${port}")" || { warn "no python3 stub listener available"; return 0; }

  # $$ is alive and is definitely not the process that owns the port.
  local out rc
  out="$(with_api_lib "${port}" "start_owns_port $$" 2>&1)"; rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    ok "start_owns_port rejects a listener that is not the launched process"
  else
    bad "start_owns_port accepted a foreign listener as its own"
  fi
  if printf '%s' "${out}" | grep -qi "hollow-restart"; then
    ok "the rejection names the hollow-restart failure"
  else
    bad "rejection message missing; output: ${out}"
  fi

  kill -KILL "${stub}" 2>/dev/null || true
}

check_stop_on_free_port_is_a_noop() {
  section "stop on a free port is an honest no-op"
  local port; port="$(free_port)"
  local out rc
  out="$(cd "${REPO_ROOT}" && env PORT="${port}" API_PORT_OVERRIDE=1 bash "${API_SH}" stop 2>&1)"; rc=$?
  if [[ "${rc}" -eq 0 ]] && printf '%s' "${out}" | grep -qi "nothing to stop"; then
    ok "stop is idempotent on a free port"
  else
    bad "stop on a free port: rc=${rc}, output: ${out}"
  fi
}

# --------------------------------------------------------------------------
# End-to-end layer: the real backend, really restarted
# --------------------------------------------------------------------------

health_header() {
  local port="$1" name="$2"
  curl -s -o /dev/null -D - --noproxy '*' --max-time 5 "http://127.0.0.1:${port}/healthz" 2>/dev/null \
    | tr -d '\r' \
    | awk -v want="$(printf '%s' "${name}" | tr 'A-Z' 'a-z'):" \
        'tolower($1) == want { $1 = ""; sub(/^[ \t]+/, ""); print }' | tail -n1
}

check_e2e_restart_replaces_the_process() {
  section "end to end: restart replaces the backend process"
  if ! command -v dotnet >/dev/null 2>&1; then
    warn "dotnet not on PATH; the end-to-end proof did NOT run"
    return 0
  fi
  if [[ -f "${REPO_ROOT}/backend/appsettings.Local.json" && "${ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG:-}" != "1" ]]; then
    warn "backend/appsettings.Local.json exists; its WatchPaths could point the test backend at the shared workspace. Re-run with ATP_WORKTREE_TEST_ALLOW_LOCAL_CONFIG=1 if its WatchPaths are safe."
    return 0
  fi

  local port; port="$(free_port)"
  local workspace
  workspace="$(mktemp -d "${TMPDIR:-/tmp}/atp-api-selfcheck-XXXXXX")" || { bad "mktemp failed"; return 0; }
  TMP_DIRS="${TMP_DIRS} ${workspace}"

  echo "  booting an isolated backend on :${port} (TaskRepository=${workspace})"
  # Warm build first: api.sh polls /healthz for 30s and a cold compile blows
  # past that window, which would fail the check for the wrong reason.
  if ! dotnet build "${REPO_ROOT}/backend/OrchestratorApi.csproj" -v quiet \
        > "${workspace}/build.log" 2>&1; then
    bad "backend build failed; see ${workspace}/build.log"
    TMP_DIRS="${TMP_DIRS// ${workspace}/}"   # keep the log for inspection
    return 0
  fi

  local api_env=(
    "PORT=${port}"
    "API_PORT_OVERRIDE=1"
    "ATP_WORKTREE_TEST_BACKEND=1"
    "TaskRepository=${workspace}"
    "Runner__Role=test-subject"
  )
  if ! ( cd "${REPO_ROOT}" && env "${api_env[@]}" bash "${API_SH}" start ) > "${workspace}/start.log" 2>&1; then
    bad "backend did not start on :${port}; see ${workspace}/start.log"
    sed 's/^/        /' "${workspace}/start.log" >&2
    TMP_DIRS="${TMP_DIRS// ${workspace}/}"
    return 0
  fi

  local pid_before start_before
  pid_before="$(health_header "${port}" X-Agent-Studio-Process-Id)"
  start_before="$(health_header "${port}" X-Agent-Studio-Process-Start)"
  if [[ -n "${pid_before}" && -n "${start_before}" ]]; then
    ok "/healthz publishes process identity (pid ${pid_before}, started ${start_before})"
  else
    bad "/healthz did not publish X-Agent-Studio-Process-Id / -Process-Start"
  fi

  local restart_out restart_rc
  restart_out="$( cd "${REPO_ROOT}" && env "${api_env[@]}" bash "${API_SH}" restart 2>&1 )"; restart_rc=$?
  if [[ "${restart_rc}" -eq 0 ]]; then
    ok "api.sh restart exits 0"
  else
    bad "api.sh restart exited ${restart_rc}; output: ${restart_out}"
  fi

  local pid_after start_after
  pid_after="$(health_header "${port}" X-Agent-Studio-Process-Id)"
  start_after="$(health_header "${port}" X-Agent-Studio-Process-Start)"

  if [[ -z "${pid_after}" ]]; then
    bad "backend is not answering /healthz after restart"
  elif [[ "${pid_after}" != "${pid_before}" && "${start_after}" != "${start_before}" ]]; then
    ok "the answering process was replaced (pid ${pid_before} -> ${pid_after}, started ${start_before} -> ${start_after})"
  else
    bad "HOLLOW RESTART: /healthz still reports pid ${pid_after} started ${start_after}"
  fi

  if [[ -n "${pid_before}" ]] && kill -0 "${pid_before}" 2>/dev/null; then
    bad "the old backend process (pid ${pid_before}) is still alive after restart"
  else
    ok "the old backend process is gone"
  fi

  if printf '%s' "${restart_out}" | grep -qi "Restart verified"; then
    ok "restart states the verified identity change"
  else
    bad "restart did not report a verified identity change; output: ${restart_out}"
  fi

  ( cd "${REPO_ROOT}" && env "${api_env[@]}" bash "${API_SH}" stop ) > "${workspace}/stop.log" 2>&1
  local stop_rc=$?
  if [[ "${stop_rc}" -eq 0 ]] && ! port_is_open "${port}"; then
    ok "stop freed :${port} and exited 0"
  else
    bad "stop rc=${stop_rc}; port still open: $(port_is_open "${port}" && echo yes || echo no)"
  fi
}

# --------------------------------------------------------------------------

echo "api.sh restart self-check"
echo "checkout: ${REPO_ROOT}"

check_stop_on_free_port_is_a_noop
check_port_match_is_anchored
check_project_scoping_is_anchored
check_pid_zero_is_never_a_kill_target
check_process_table_is_refreshed
check_kill_tree_takes_children
check_stop_fails_loudly_when_kill_does_not_work
check_stop_is_honest_when_discovery_is_blind
check_start_refuses_foreign_owner
check_start_rejects_a_port_it_did_not_take
if [[ "${RUN_E2E}" -eq 1 ]]; then
  check_e2e_restart_replaces_the_process
else
  section "end to end: restart replaces the backend process"
  warn "--quick given; the end-to-end proof did NOT run"
fi

echo
echo "== summary: ${pass} passed, ${fail} failed, ${skip} skipped =="
[[ "${fail}" -eq 0 ]]
