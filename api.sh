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
# Lifecycle contract (AGT-2678). `stop` and `restart` used to be able to lie:
# they printed "API stopped." and then "API is successfully started and
# healthy!" while the OLD OrchestratorApi process kept serving the port. Three
# things made that possible, and all three are closed here:
#
#   1. Stop killed ONE pid and never checked the outcome. On POSIX a plain
#      `kill` on the `dotnet run` wrapper leaves the OrchestratorApi child
#      holding the port and the build output DLLs; the wrapper dies, the
#      server does not. Stop now kills whole process TREES and every process
#      of THIS checkout, not just the tracked pid.
#   2. Discovery was single-source and unanchored. If lsof/ss/netstat was
#      missing, unreadable, or the process lived in another pid namespace
#      (WSL vs Windows, container), `listener_pid` returned empty and the
#      sweep silently did nothing. Ownership discovery is now multi-source,
#      the port match is anchored (":5030" also matches ":50300"), and the
#      authoritative "is the port free" answer is a TCP connect probe that
#      does not depend on seeing any pid at all.
#   3. Start trusted a 200 from the port. After a hollow stop the new
#      `dotnet run` dies with "address already in use" while the OLD process
#      answers /healthz, so the health poll passed and the script reported
#      success. Start now refuses to boot onto an occupied port, and after
#      the health poll it proves the answering process is the one it just
#      launched.
#
# Every command therefore ends in a verified state or exits non-zero. Silence
# is never treated as success. `tools/api-restart-selfcheck.sh` is the
# executable proof of this contract.
#
# Usage:
#   ./api.sh start
#   ./api.sh stop
#   ./api.sh restart
#   ./api.sh status
#
# Sourcing this file with ATP_API_SH_LIB=1 defines the helpers without running
# a command, which is how the self-check exercises them.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
# Default port is pinned to the checkout folder name: stable runs on 5031,
# dev on 5030. This matters because a coding-agent session running INSIDE
# one backend may invoke api.sh from a SIBLING checkout (e.g. claude doing
# `bash api.sh restart` from inside the dev folder while it was spawned by
# the stable backend). The child process inherits PORT from its parent;
# without a guard, dev's api.sh would happily start dev's exe on stable's
# port 5031 - exactly what happened during the suchbox-orphan incident on
# 2026-05-07. The guard below refuses any inherited PORT that disagrees
# with this checkout's pinned default unless API_PORT_OVERRIDE=1 is set,
# which is the explicit "I know what I'm doing" escape hatch.
case "$(basename "${SCRIPT_DIR}")" in
  *-stable) DEFAULT_PORT=5031 ;;
  *)        DEFAULT_PORT=5030 ;;
esac

if [[ -n "${PORT:-}" && "${PORT}" != "${DEFAULT_PORT}" ]]; then
  if [[ "${API_PORT_OVERRIDE:-}" != "1" ]]; then
    echo "ERROR: api.sh in ${SCRIPT_DIR}" >&2
    echo "       has DEFAULT_PORT=${DEFAULT_PORT} (pinned by checkout folder name)" >&2
    echo "       but the environment carries PORT=${PORT}." >&2
    echo "       Refusing to bind a sibling checkout's exe onto this port." >&2
    echo "       If this is intentional, re-run with API_PORT_OVERRIDE=1." >&2
    exit 1
  fi
  echo "WARN: API_PORT_OVERRIDE=1; using non-default PORT=${PORT}." >&2
fi
PORT="${PORT:-${DEFAULT_PORT}}"
BASE_URL="http://127.0.0.1:${PORT}"
HEALTH_URL="${BASE_URL}/healthz"
PROJECT_FILE="${SCRIPT_DIR}/backend/OrchestratorApi.csproj"
# State files are per-port once the port leaves the pinned default. One
# checkout can legitimately run its pinned backend and an isolated worktree
# test backend on a dynamic port at the same time (ASS-1715); sharing one pid
# file made the second boot overwrite the first one's tracked pid, so a later
# stop aimed at the wrong process and left the other one running. The pinned
# port keeps the historical `.api.pid` / `.api.log.*` names that
# scripts/supervisor/dev-lifecycle.sh and the setup docs refer to.
if [[ "${PORT}" == "${DEFAULT_PORT}" ]]; then
  STATE_SUFFIX=""
else
  STATE_SUFFIX=".${PORT}"
fi
PID_FILE="${SCRIPT_DIR}/.api${STATE_SUFFIX}.pid"
LOG_OUT="${SCRIPT_DIR}/.api${STATE_SUFFIX}.log.out"
LOG_ERR="${SCRIPT_DIR}/.api${STATE_SUFFIX}.log.err"

# Timeouts. Validated here rather than at the arithmetic site, where a
# non-numeric override would abort the script with a bash syntax error in the
# middle of a stop.
numeric_or_default() {
  # numeric_or_default <name> <value> <default>
  if [[ "$2" =~ ^[0-9]+$ ]] && (( $2 > 0 )); then
    printf '%s' "$2"
  else
    [[ -z "$2" ]] || echo "WARN: ignoring non-numeric $1='$2'; using $3." >&2
    printf '%s' "$3"
  fi
}
# Seconds to keep re-killing and re-checking before stop declares failure.
STOP_TIMEOUT="$(numeric_or_default ATP_API_STOP_TIMEOUT "${ATP_API_STOP_TIMEOUT:-}" 20)"
# Seconds to wait for /healthz after a launch.
HEALTH_TIMEOUT="$(numeric_or_default ATP_API_HEALTH_TIMEOUT "${ATP_API_HEALTH_TIMEOUT:-}" 30)"

is_windows() {
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

# --------------------------------------------------------------------------
# Process discovery
#
# PROC_TABLE holds one "pid<TAB>ppid<TAB>command" line per process. It is
# captured once per sweep instead of shelling out per pid, because on Windows
# every lookup costs a CIM query. refresh_process_table is called before each
# decision so a sweep never acts on a stale snapshot.
# --------------------------------------------------------------------------
PROC_TABLE=""

# A pid this script is ever allowed to signal. Rejecting 0 and 1 is not
# pedantry: `kill -TERM 0` signals the caller's ENTIRE process group, which
# would take down this script, the shell that ran it and the agent session
# around it. Both values reach the kill path realistically. `netstat -ano`
# reports 0 as the owning pid of some listening sockets, and a truncated or
# half-written .api.pid reads back as 0.
sane_pid() {
  local pid="${1:-}"
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 1
  (( pid > 1 ))
}

# Filter a pid list on stdin down to the ones we may act on.
sane_pids() {
  awk '/^[0-9]+$/ && $1 > 1 { print }'
}

refresh_process_table() {
  # Reset first. Without this the Windows `ps -W` fallback below only ever
  # populates the table once (its guard is "table is empty"), so every later
  # sweep would decide on a snapshot taken before the kills, or before the
  # process it is about to verify even existed.
  PROC_TABLE=""
  if is_windows; then
    if command -v powershell.exe >/dev/null 2>&1; then
      PROC_TABLE="$(powershell.exe -NoProfile -NonInteractive -Command \
        'Get-CimInstance Win32_Process | ForEach-Object { "{0}`t{1}`t{2}" -f $_.ProcessId, $_.ParentProcessId, $_.CommandLine }' \
        2>/dev/null | tr -d '\r')"
    fi
    if [[ -z "${PROC_TABLE}" ]]; then
      # Fallback without PowerShell: MSYS `ps -W` lists native processes too.
      # Column 4 is the WINPID, which is the id netstat and taskkill speak.
      # Parent ids in that listing belong to the MSYS domain, so they are
      # dropped (reported as 0); on Windows the tree kill is done by
      # `taskkill //T` anyway, which does not need our ppid map.
      PROC_TABLE="$(ps -W 2>/dev/null | awk 'NR > 1 && $4 ~ /^[0-9]+$/ {
          pid = $4; cmd = "";
          for (i = 8; i <= NF; i++) cmd = cmd (cmd == "" ? "" : " ") $i;
          print pid "\t0\t" cmd
        }')"
    fi
  else
    # -ww: never truncate the command line. project_pids matches on the
    # checkout path and the port, both of which sit far to the right of it.
    PROC_TABLE="$(ps -ww -eo pid=,ppid=,args= 2>/dev/null | awk '{
        pid = $1; ppid = $2; $1 = ""; $2 = "";
        sub(/^[ \t]+/, "");
        print pid "\t" ppid "\t" $0
      }')"
  fi
}

proc_command() {
  local pid="$1"
  sane_pid "${pid}" || return 0
  printf '%s\n' "${PROC_TABLE}" | awk -F'\t' -v p="${pid}" '$1 == p { print $3; exit }'
}

# Describe a pid for operator-facing output. Never empty, so a diagnostic line
# never degrades into "PID 28116 ()".
proc_label() {
  local pid="$1" cmd
  cmd="$(proc_command "${pid}")"
  [[ -n "${cmd}" ]] || cmd="command not visible from this shell"
  printf '%.160s' "${cmd}"
}

children_of() {
  local pid="$1"
  sane_pid "${pid}" || return 0
  printf '%s\n' "${PROC_TABLE}" | awk -F'\t' -v p="${pid}" '$2 == p { print $1 }'
}

# Whole subtree below pid, deepest first, so a kill sweep takes the children
# out before the parent that could otherwise reap or re-parent them.
descendants_of() {
  local pid="$1"
  sane_pid "${pid}" || return 0
  local frontier="${pid}" seen=" ${pid} " out="" next kid
  while [[ -n "${frontier}" ]]; do
    next=""
    for kid in ${frontier}; do
      local grandkid
      for grandkid in $(children_of "${kid}"); do
        case "${seen}" in
          *" ${grandkid} "*) continue ;;
        esac
        seen="${seen}${grandkid} "
        next="${next} ${grandkid}"
        out="${grandkid}
${out}"
      done
    done
    frontier="${next}"
  done
  printf '%s' "${out}" | sane_pids || true
}

# Returns 0 if PID is alive, 1 otherwise. Works on Git Bash & POSIX.
# `kill -0` is asked first on every platform because a pid we launched
# ourselves from Git Bash is an MSYS id that tasklist does not know, while a
# pid discovered through netstat is a native id that only tasklist knows.
pid_alive() {
  local pid="$1"
  sane_pid "${pid}" || return 1
  if kill -0 "${pid}" 2>/dev/null; then return 0; fi
  if is_windows; then
    # The filter already selects the pid, so presence of any task row is the
    # answer. Grepping for the number instead would match the Session# column.
    tasklist //FI "PID eq ${pid}" 2>/dev/null | grep -qvi "No tasks are running" \
      && tasklist //FI "PID eq ${pid}" 2>/dev/null | grep -qi "^[a-z0-9]"
  else
    return 1
  fi
}

# Liveness of the process THIS run launched. A child that is disowned but not
# reaped stays in the process table as a zombie until this shell exits, and
# `kill -0` reports a zombie as alive. Treating that as running is how a
# launcher that died immediately on "address already in use" would go unnoticed
# until the health timeout expired.
launcher_alive() {
  local pid="$1"
  pid_alive "${pid}" || return 1
  local state=""
  if [[ -r "/proc/${pid}/stat" ]]; then
    state="$(sed 's/^.*) //' "/proc/${pid}/stat" 2>/dev/null | awk '{ print $1 }')"
  elif ! is_windows; then
    state="$(ps -o stat= -p "${pid}" 2>/dev/null | tr -d ' ')"
  fi
  case "${state}" in
    Z*) return 1 ;;
    *)  return 0 ;;
  esac
}

# Stable identity of a process: pid alone is not enough, because the OS
# recycles pids. pid + start time is what "restart replaced the process"
# is asserted against.
proc_start_token() {
  local pid="$1"
  sane_pid "${pid}" || { printf 'unknown'; return 0; }
  local token=""
  if [[ -r "/proc/${pid}/stat" ]]; then
    # Field 22 is starttime in clock ticks. The comm field may contain
    # spaces and parentheses, so everything up to the last ')' is dropped
    # first; starttime is then field 20 of the remainder.
    token="$(sed 's/^.*) //' "/proc/${pid}/stat" 2>/dev/null | awk '{ print $20 }')"
  fi
  if [[ -z "${token}" ]] && is_windows && command -v powershell.exe >/dev/null 2>&1; then
    token="$(powershell.exe -NoProfile -NonInteractive -Command \
      "(Get-CimInstance Win32_Process -Filter \"ProcessId=${pid}\").CreationDate" 2>/dev/null | tr -d ' \r\n')"
  fi
  if [[ -z "${token}" ]]; then
    token="$(ps -o lstart= -p "${pid}" 2>/dev/null | tr -s ' ' '_' | tr -d '\n')"
  fi
  [[ -n "${token}" ]] || token="unknown"
  printf '%s' "${token}"
}

kill_tree() {
  local pid="$1"
  sane_pid "${pid}" || return 0
  if is_windows; then
    # //T takes the whole tree, //F skips the polite request the .NET host
    # ignores once it is mid-shutdown. taskkill speaks NATIVE pids only, but
    # the one pid this script owns in the MSYS domain is the launcher from
    # `$!`, so the MSYS kill is attempted as well. Whichever domain the pid
    # belongs to, exactly one of the two lands.
    taskkill //F //T //PID "${pid}" >/dev/null 2>&1 || true
    kill -TERM "${pid}" 2>/dev/null || true
    sleep 0.3
    kill -KILL "${pid}" 2>/dev/null || true
    return 0
  fi
  local tree p
  tree="$(descendants_of "${pid}")"
  for p in ${tree} "${pid}"; do
    kill -TERM "${p}" 2>/dev/null || true
  done
  # Give the host its graceful-shutdown window before escalating; a SIGKILL
  # straight away is what leaves half-written state behind.
  local waited=0
  while (( waited < 30 )); do
    pid_alive "${pid}" || break
    sleep 0.1
    waited=$((waited + 1))
  done
  for p in ${tree} "${pid}"; do
    kill -KILL "${p}" 2>/dev/null || true
  done
}

# --------------------------------------------------------------------------
# Port ownership
# --------------------------------------------------------------------------

# Ground truth for "is anything serving this port", independent of whether we
# can see the owning pid. A stop that cannot answer this question honestly is
# the whole bug, so this deliberately does not rely on lsof/ss/netstat.
port_open() {
  # bash's own /dev/tcp costs no fork when the build supports it.
  if (exec 3<>"/dev/tcp/127.0.0.1/${PORT}") 2>/dev/null; then
    return 0
  fi
  if ! command -v curl >/dev/null 2>&1; then
    echo "WARN: neither bash /dev/tcp nor curl is available, so this shell cannot" >&2
    echo "      determine whether port ${PORT} is free. Treating it as occupied." >&2
    return 0
  fi
  # --noproxy: an inherited http_proxy would otherwise make curl report the
  # proxy's reachability instead of the port's.
  curl -s -o /dev/null --noproxy '*' --max-time 3 "${BASE_URL}/" >/dev/null 2>&1
  case "$?" in
    7)  return 1 ;;            # connection refused: nothing is listening
    0)  return 0 ;;            # answered
    28|52|56) return 0 ;;      # accepted the connection, then stalled/reset
    # Anything else means the probe itself failed (curl too old for --noproxy,
    # a local resolver error). This function is the ground truth the whole
    # stop verification rests on, so an inconclusive probe must read as
    # "still occupied". Failing open here would restore the exact bug this
    # script exists to prevent.
    *)  return 0 ;;
  esac
}

# Every pid listening on $PORT. The port match is anchored on purpose: an
# unanchored ":5030" also matches ":50300" and would report a stranger's pid.
# Sources are tried in order and merged, because any single one of them can be
# missing (lsof not installed), blind (ss without privileges cannot attribute
# another user's socket) or absent from PATH.
listener_pids() {
  local out=""
  if is_windows; then
    out="$(netstat -ano 2>/dev/null | awk -v re=":${PORT}\$" '
        $1 == "TCP" && $2 ~ re && $4 == "LISTENING" { print $5 }')"
    if [[ -z "${out}" ]] && command -v powershell.exe >/dev/null 2>&1; then
      out="$(powershell.exe -NoProfile -NonInteractive -Command \
        "Get-NetTCPConnection -LocalPort ${PORT} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess" \
        2>/dev/null | tr -d '\r')"
    fi
  else
    if command -v lsof >/dev/null 2>&1; then
      out="$(lsof -nP -iTCP:"${PORT}" -sTCP:LISTEN -t 2>/dev/null)"
    fi
    if [[ -z "${out}" ]] && command -v ss >/dev/null 2>&1; then
      out="$(ss -lntpH "sport = :${PORT}" 2>/dev/null | grep -oE 'pid=[0-9]+' | cut -d= -f2)"
    fi
    if [[ -z "${out}" ]] && command -v fuser >/dev/null 2>&1; then
      out="$(fuser -n tcp "${PORT}" 2>/dev/null | tr -s ' ' '\n')"
    fi
    if [[ -z "${out}" ]] && command -v netstat >/dev/null 2>&1; then
      out="$(netstat -lntp 2>/dev/null | awk -v re=":${PORT}\$" '
          $4 ~ re { split($7, a, "/"); if (a[1] ~ /^[0-9]+$/) print a[1] }')"
    fi
  fi
  printf '%s\n' "${out}" | tr -d ' \r' | sane_pids | sort -u -n || true
}

# Path spellings of this checkout's backend folder as they can appear in a
# command line. Under Git Bash the shell knows /c/dev/foo while the native
# process reports C:\dev\foo. The trailing separator is what makes the match
# anchored: a bare checkout path is a prefix of a sibling worktree
# (".../checkout" matches ".../checkout-worktree"), while ".../checkout/backend"
# cannot be. Both the csproj passed to `dotnet run` and the built host binary
# live under <checkout>/backend, so this loses nothing.
checkout_path_variants() {
  printf '%s\n' "${SCRIPT_DIR}/backend/"
  if is_windows; then
    if command -v cygpath >/dev/null 2>&1; then
      printf '%s\\backend\\' "$(cygpath -w "${SCRIPT_DIR}" 2>/dev/null)"
    fi
    # Git Bash reports the POSIX form while the process reports the native one;
    # cover the backslash spelling even when cygpath is unavailable.
    printf '%s\n' "${SCRIPT_DIR//\//\\}\\backend\\"
  fi
}

# Backend processes belonging to THIS checkout and THIS port: the `dotnet run`
# wrapper, the OrchestratorApi host it execs, and anything either of them
# spawned. Scoping on the checkout path is what keeps a stop in the dev
# checkout from ever touching stable's backend; scoping on the port is what
# keeps it from touching an isolated worktree test backend of the same
# checkout that happens to run on a dynamic port.
project_pids() {
  local variants pid_list=""
  variants="$(checkout_path_variants)"
  local variant
  while IFS= read -r variant; do
    [[ -n "${variant}" ]] || continue
    pid_list="${pid_list}
$(printf '%s\n' "${PROC_TABLE}" | awk -F'\t' -v needle="${variant}" -v port="${PORT}" '
        # The port is matched on a digit boundary. An unanchored ":5030" also
        # matches ":50301", which is inside the ephemeral range that
        # scripts/find-free-port.mjs draws worktree test ports from, so the
        # sloppy match would let a stop on the pinned port kill an isolated
        # test backend of the same checkout.
        function has_port(cmd,   at, tail) {
          at = index(cmd, ":" port);
          while (at > 0) {
            tail = substr(cmd, at + length(port) + 1, 1);
            if (tail !~ /[0-9]/) return 1;
            cmd = substr(cmd, at + 1);
            at = index(cmd, ":" port);
          }
          return 0;
        }
        {
          cmd = $3;
          if (index(cmd, needle) == 0) next;
          if (index(cmd, "OrchestratorApi") == 0 && index(cmd, "dotnet") == 0) next;
          # Only has_port decides: the base URL is itself an unanchored
          # substring of a longer port ("http://127.0.0.1:5030" is a prefix of
          # "http://127.0.0.1:50301"), so testing it would reopen the leak.
          if (has_port(cmd) == 0) next;
          print $1;
        }')"
  done <<< "${variants}"
  printf '%s\n' "${pid_list}" | sane_pids | sort -u -n || true
}

# The pid file is a hint, not evidence. A stale file can point at a recycled
# pid that now belongs to an unrelated process, so it is only accepted as a
# kill target when the process still looks like ours. This check FAILS CLOSED:
# a command line we cannot read is not proof of ownership, and force-killing
# a whole process tree on the strength of a number in a stale file is how a
# lifecycle script takes out something it never started. The pid file is only
# ever an ADDITION to discovery, so dropping it here costs nothing when the
# process really is ours.
pid_file_target() {
  [[ -f "${PID_FILE}" ]] || return 0
  local stored
  stored="$(tr -d ' \r\n' < "${PID_FILE}" 2>/dev/null || true)"
  sane_pid "${stored}" || return 0
  pid_alive "${stored}" || return 0

  # Already established as ours by discovery: nothing more to prove.
  local known
  known="$( { listener_pids; project_pids; } | sane_pids )"
  case "
${known}
" in
    *"
${stored}
"*) printf '%s\n' "${stored}"; return 0 ;;
  esac

  local cmd; cmd="$(proc_command "${stored}")"
  [[ -n "${cmd}" ]] || return 0
  local variant matched=0
  while IFS= read -r variant; do
    [[ -n "${variant}" ]] || continue
    case "${cmd}" in *"${variant}"*) matched=1 ;; esac
  done <<< "$(checkout_path_variants)"
  [[ "${matched}" -eq 1 ]] || return 0
  printf '%s\n' "${stored}"
}

# Union of everything a stop has to clear, deepest descendants included.
stop_targets() {
  local roots pid all=""
  roots="$( { listener_pids; project_pids; pid_file_target; } | sane_pids | sort -u -n )"
  for pid in ${roots}; do
    all="${all}
${pid}
$(descendants_of "${pid}")"
  done
  printf '%s\n' "${all}" | sane_pids | sort -u -n || true
}

# Targets used to VERIFY a stop. Deliberately narrower than stop_targets: it
# re-derives from live discovery only, never from the pid file, so a pid-domain
# mismatch cannot make a successful stop look like a failure.
remaining_targets() {
  { listener_pids; project_pids; } | sane_pids | sort -u -n || true
}

# --------------------------------------------------------------------------
# Health probes
# --------------------------------------------------------------------------

health_code() {
  # curl already prints 000 on a connection failure AND exits non-zero, so a
  # `|| echo 000` would append a second one and produce "000000" in operator
  # output. Normalise instead.
  local code
  code="$(curl -s -o /dev/null --noproxy '*' -w '%{http_code}' --max-time "${1:-2}" "${HEALTH_URL}" 2>/dev/null)" || true
  [[ "${code}" =~ ^[0-9]{3}$ ]] || code="000"
  printf '%s' "${code}"
}

health_headers() {
  curl -s -o /dev/null -D - --noproxy '*' --max-time "${1:-3}" "${HEALTH_URL}" 2>/dev/null | tr -d '\r' || true
}

header_value() {
  # header_value <name> <<< "<raw headers>"
  awk -v want="$(printf '%s' "$1" | tr 'A-Z' 'a-z'):" \
    'tolower($1) == want { $1 = ""; sub(/^[ \t]+/, ""); print }' | tail -n1
}

# "<process-id>|<process-start>" as reported by /healthz, empty when the
# answering server does not publish it. This is the identity signal that
# survives a pid-namespace boundary: WSL cannot see a Windows pid, but both
# see the same HTTP response.
health_identity() {
  local hdrs; hdrs="$(health_headers "${1:-3}")"
  [[ -n "${hdrs}" ]] || return 0
  local pid start
  pid="$(printf '%s\n' "${hdrs}" | header_value X-Agent-Studio-Process-Id)"
  start="$(printf '%s\n' "${hdrs}" | header_value X-Agent-Studio-Process-Start)"
  [[ -n "${pid}${start}" ]] || return 0
  printf '%s|%s' "${pid}" "${start}"
}

# Is whatever answers this port an Agent Studio backend at all? Used to tell
# "our backend is up" from "a stranger squats on the port", which start must
# never resolve by killing.
serves_agent_studio() {
  local hdrs; hdrs="$(health_headers 3)"
  printf '%s\n' "${hdrs}" | grep -qi '^x-agent-studio-'
}

# --------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------

# Print the API status. Sets globals: STATUS_RUNNING, STATUS_HEALTHY, STATUS_PID, STATUS_MSG.
get_status() {
  STATUS_RUNNING=0
  STATUS_HEALTHY=0
  STATUS_PID=""
  STATUS_MSG="stopped"

  refresh_process_table

  local owners; owners="$(remaining_targets)"
  if port_open || [[ -n "${owners}" ]]; then
    STATUS_RUNNING=1
    STATUS_PID="$(printf '%s\n' "${owners}" | head -n1)"
  fi

  if [[ "${STATUS_RUNNING}" -eq 1 ]]; then
    local code; code="$(health_code)"
    local who="PID: ${STATUS_PID:-not visible from this shell}"
    if [[ "${code}" == "200" ]]; then
      STATUS_HEALTHY=1
      STATUS_MSG="running and healthy (${who})"
    else
      STATUS_MSG="running but unhealthy (HTTP ${code}, ${who})"
    fi
  fi
}

cmd_status() {
  get_status
  echo "API STATUS: ${STATUS_MSG}"
  if [[ "${STATUS_RUNNING}" -eq 1 ]]; then
    local pid
    for pid in $(remaining_targets); do
      echo "  owner PID ${pid}: $(proc_label "${pid}")"
    done
    local ident; ident="$(health_identity)"
    [[ -n "${ident}" ]] && echo "  process identity (from /healthz): ${ident}"
  fi
  return 0
}

# Kill everything holding the port, then PROVE the port is free. Returns 1
# with a diagnosis when it cannot, because a stop that reports success it
# cannot back up is exactly what let an old process keep serving.
cmd_stop() {
  refresh_process_table

  local targets; targets="$(stop_targets)"
  if [[ -z "${targets}" ]] && ! port_open; then
    rm -f "${PID_FILE}"
    echo "API is not running on port ${PORT} (nothing to stop)."
    return 0
  fi

  local deadline=$(( SECONDS + STOP_TIMEOUT ))
  local announced=" "
  while :; do
    local pid
    for pid in ${targets}; do
      case "${announced}" in
        *" ${pid} "*) ;;
        *) echo "Stopping PID ${pid} on port ${PORT} ($(proc_label "${pid}"))..."
           announced="${announced}${pid} " ;;
      esac
      kill_tree "${pid}"
    done
    sleep 0.5
    refresh_process_table
    if [[ -z "$(remaining_targets)" ]] && ! port_open; then
      rm -f "${PID_FILE}"
      echo "API stopped: port ${PORT} is free and no backend process of this checkout remains."
      return 0
    fi
    (( SECONDS < deadline )) || break
    targets="$(stop_targets)"
  done

  echo "ERROR: stop could not free port ${PORT} within ${STOP_TIMEOUT}s." >&2
  local pid
  for pid in $(remaining_targets); do
    echo "       still alive: PID ${pid} ($(proc_label "${pid}"))" >&2
  done
  if port_open && [[ -z "$(remaining_targets)" ]]; then
    echo "       The port still accepts TCP connections but no owning PID is" >&2
    echo "       visible from this shell. That happens across a process boundary:" >&2
    echo "       a backend started on Windows cannot be seen (or killed) from WSL," >&2
    echo "       a container, or another user's session. Stop it where it was" >&2
    echo "       started, then re-run." >&2
  fi
  echo "       NOT starting anything: the old process is still serving." >&2
  return 1
}

# ADR-0044 boot policy gate, factored out of cmd_start so that restart can
# consult it BEFORE stopping anything. Inline, it produced the worst possible
# order: restart tore the backend down and only then refused to start it,
# leaving the API down and the script aborted.
# Per-port log files accumulate one pair per port. The pinned port reuses one
# pair forever, but the isolated worktree test stack draws a fresh random port
# on every run, so without a bound the checkout would collect a new pair each
# time. Drop per-port logs nothing has touched for a week; the pinned-port
# files (.api.log.out / .api.log.err) never match this pattern.
prune_stale_port_logs() {
  command -v find >/dev/null 2>&1 || return 0
  find "${SCRIPT_DIR}" -maxdepth 1 -type f -name '.api.*.log.*' -mtime +7 \
    -delete 2>/dev/null || true
}

# The boot-mode note is informational and belongs to the run, not to the call.
# restart consults the gate before the stop and cmd_start consults it again, so
# without this it would print twice.
BOOT_POLICY_ANNOUNCED=0
boot_note() {
  [[ "${BOOT_POLICY_ANNOUNCED}" -eq 0 ]] || return 0
  BOOT_POLICY_ANNOUNCED=1
  echo "$1"
}

assert_boot_policy() {
  # ADR-0044: dev backend boot policy gate. The dev checkout is the regression-
  # test target, not a second pickup driver on the shared workspace. The
  # Playwright fixture (`scripts/supervisor/dev-lifecycle.sh`) is the one
  # legitimate caller that brings dev up; it exports
  # ATP_DEV_BACKEND_FROM_FIXTURE=1 to bypass this gate. Direct human
  # invocation needs ATP_ALLOW_DEV_BACKEND=1 as an explicit policy
  # acknowledgement. Anything else refuses to boot so that an interactive
  # session or a stray watchdog cannot silently restart dev's pickup loop
  # on a shared workspace. The stable checkout (folder name ends in -stable)
  # is unaffected by this gate.
  case "$(basename "${SCRIPT_DIR}")" in
    *-stable) ;;
    *)
      if [[ "${ATP_DEV_BACKEND_FROM_FIXTURE:-}" != "1" \
         && "${ATP_ALLOW_DEV_BACKEND:-}" != "1" \
         && "${ATP_WORKTREE_TEST_BACKEND:-}" != "1" ]]; then
        echo "ERROR: refusing to start the dev backend." >&2
        echo "       AGENTS.md 'Dev backend lifecycle: Playwright-only' (ADR-0044) says the dev" >&2
        echo "       checkout is offline by default. The only path that may bring it up is the" >&2
        echo "       Playwright dev-backend fixture, which routes through" >&2
        echo "       scripts/supervisor/dev-lifecycle.sh and sets ATP_DEV_BACKEND_FROM_FIXTURE=1." >&2
        echo "" >&2
        echo "       To boot dev manually for interactive debugging, re-run with" >&2
        echo "       ATP_ALLOW_DEV_BACKEND=1 set in the environment as a policy acknowledgement:" >&2
        echo "         ATP_ALLOW_DEV_BACKEND=1 ./api.sh start" >&2
        echo "" >&2
        echo "       To boot an ISOLATED worktree test backend on a dynamic port, use" >&2
        echo "       scripts/worktree-test-stack.sh (sets ATP_WORKTREE_TEST_BACKEND=1 +" >&2
        echo "       an isolated TaskRepository)." >&2
        return 1
      fi
      if [[ "${ATP_WORKTREE_TEST_BACKEND:-}" == "1" ]]; then
        # ASS-1715: isolated per-worktree test backend on a dynamic port. The
        # dev-backend gate exists to stop a SECOND pickup driver from booting on
        # the SHARED workspace. A worktree test backend is only safe when it
        # points at an isolated workspace, so refuse unless TaskRepository is set
        # to an isolated path (the lifecycle script always points it at a unique
        # temp dir). This mirrors the in-process xunit isolation guard in
        # backend/Program.cs.
        if [[ -z "${TaskRepository:-}" ]]; then
          echo "ERROR: ATP_WORKTREE_TEST_BACKEND=1 requires an isolated TaskRepository." >&2
          echo "       Refusing to boot a worktree test backend against the shared workspace." >&2
          echo "       Set TaskRepository to a dedicated temp dir (scripts/worktree-test-stack.sh" >&2
          echo "       does this for you)." >&2
          return 1
        fi
        boot_note "[api.sh] worktree test backend boot: isolated TaskRepository=${TaskRepository} on :${PORT}."
      elif [[ "${ATP_DEV_BACKEND_FROM_FIXTURE:-}" == "1" ]]; then
        boot_note "[api.sh] dev backend boot: Playwright fixture (ATP_DEV_BACKEND_FROM_FIXTURE=1)."
      else
        boot_note "[api.sh] dev backend boot: operator acknowledged (ATP_ALLOW_DEV_BACKEND=1)."
      fi
      ;;
  esac

}

cmd_start() {
  assert_boot_policy || return 1

  refresh_process_table

  # Pre-flight. An occupied port has exactly three outcomes, and "launch
  # anyway and let the health probe decide" is not one of them: the probe
  # would be answered by whoever already owns the port.
  if port_open; then
    local code; code="$(health_code)"
    local owners; owners="$(project_pids | tr '\n' ' ')"
    if [[ -n "${owners// /}" ]] || serves_agent_studio; then
      if [[ "${code}" == "200" ]]; then
        if [[ -n "${owners// /}" ]]; then
          echo "API is already running and healthy on port ${PORT} (PID: ${owners})."
        else
          # serves_agent_studio only proves that SOME Agent Studio backend
          # answers, not that it is this checkout's. Do not round that up.
          echo "An Agent Studio backend is already healthy on port ${PORT}."
          echo "Could not confirm it belongs to ${SCRIPT_DIR}: no owning PID is visible from this shell."
          echo "Identity reported by /healthz: $(health_identity)"
        fi
        return 0
      fi
      echo "API is running but unhealthy (HTTP ${code}). Stopping first..."
      cmd_stop || return 1
    else
      echo "ERROR: port ${PORT} is already owned by a process that is not this checkout's backend." >&2
      local pid listeners
      listeners="$(listener_pids)"
      for pid in ${listeners}; do
        echo "       PID ${pid}: $(proc_label "${pid}")" >&2
      done
      [[ -n "${listeners}" ]] || \
        echo "       The owning PID is not visible from this shell (different user, container, or WSL/Windows boundary)." >&2
      echo "       Refusing to start: a second bind would fail and the foreign process" >&2
      echo "       would keep answering /healthz, which reads as a successful start." >&2
      echo "       Free the port (or set PORT to a free one) and re-run." >&2
      return 1
    fi
  fi

  echo "Starting API on ${BASE_URL}..."
  prune_stale_port_logs
  # Log rotation, not truncate. The truncate-on-start path lost three crash
  # traces in a row when the backend died between restarts: by the time we
  # ran tail on the log it was already empty. Move the previous run's logs
  # to .api.log.out.<ts>.bak / .api.log.err.<ts>.bak so the final seconds
  # of the dead process remain readable. Keep the last 5 rotations per
  # stream; older ones drop off so the workspace does not grow unbounded.
  if [[ -s "${LOG_OUT}" ]] || [[ -s "${LOG_ERR}" ]]; then
    local rot_ts
    rot_ts="$(date -u +%Y%m%dT%H%M%SZ)"
    [[ -s "${LOG_OUT}" ]] && mv "${LOG_OUT}" "${LOG_OUT}.${rot_ts}.bak"
    [[ -s "${LOG_ERR}" ]] && mv "${LOG_ERR}" "${LOG_ERR}.${rot_ts}.bak"
    # Trim to the 5 most recent .bak files per stream.
    # A while-read loop rather than `xargs -r`: -r is GNU-only, and xargs
    # word-splits, which mangles a checkout path containing a space.
    local old_log
    ls -1t "${LOG_OUT}".*.bak 2>/dev/null | tail -n +6 | while IFS= read -r old_log; do
      [[ -n "${old_log}" ]] && rm -f -- "${old_log}"
    done
    ls -1t "${LOG_ERR}".*.bak 2>/dev/null | tail -n +6 | while IFS= read -r old_log; do
      [[ -n "${old_log}" ]] && rm -f -- "${old_log}"
    done
  fi
  : > "${LOG_OUT}"
  : > "${LOG_ERR}"

  # Background-launch dotnet detached from this shell. nohup keeps it alive
  # after the script exits; the redirects keep stdout/stderr persistent.
  nohup dotnet run --project "${PROJECT_FILE}" --urls "${BASE_URL}" \
    > "${LOG_OUT}" 2> "${LOG_ERR}" &
  local launched_pid=$!
  disown 2>/dev/null || true

  echo "${launched_pid}" > "${PID_FILE}"
  echo "API process started with PID: ${launched_pid}. Waiting for health check..."

  local attempts=0 max_attempts=$(( HEALTH_TIMEOUT * 2 ))
  while (( attempts < max_attempts )); do
    sleep 0.5
    if [[ "$(health_code 1)" == "200" ]]; then
      refresh_process_table
      if start_owns_port "${launched_pid}"; then
        echo "API is successfully started and healthy!"
        return 0
      fi
      return 1
    fi
    # A launcher that exited before the port came up is a hard failure; the
    # classic case is "address already in use" against a leftover process.
    if ! launcher_alive "${launched_pid}"; then
      refresh_process_table
      if [[ -z "$(project_pids)" ]]; then
        echo "ERROR: the launched backend process exited before it became healthy." >&2
        echo "       Last lines of ${LOG_ERR}:" >&2
        tail -n 20 "${LOG_ERR}" 2>/dev/null | sed 's/^/       /' >&2
        echo "       Last lines of ${LOG_OUT}:" >&2
        tail -n 20 "${LOG_OUT}" 2>/dev/null | sed 's/^/       /' >&2
        rm -f "${PID_FILE}"
        return 1
      fi
    fi
    attempts=$((attempts + 1))
  done

  echo "ERROR: API started but did not become healthy within ${HEALTH_TIMEOUT} seconds." >&2
  echo "       Check ${LOG_OUT} and ${LOG_ERR} for details." >&2
  return 1
}

# Prove that the process answering /healthz is the one this run launched, and
# not an older process that never died. Ownership is accepted through either
# of two independent routes, because neither works on every platform:
#   - the listener is the launched pid or one of its descendants (POSIX);
#   - the listener is a backend process of THIS checkout on THIS port, which
#     is how a Git Bash launch is recognised even though $! is an MSYS id and
#     netstat reports a native one.
start_owns_port() {
  local launched_pid="$1"
  local listeners; listeners="$(listener_pids)"
  local kids; kids="$(descendants_of "${launched_pid}")"
  local ours; ours="${launched_pid}
${kids}"
  if [[ -z "${kids}" ]]; then
    # No descendant map: under Git Bash the launched pid is an MSYS id while
    # netstat reports native ids, so parentage cannot prove anything. Fall back
    # to "a backend process of this checkout on this port", which the pre-flight
    # established was not running moments ago. This fallback is deliberately
    # NOT used when parentage is available, because there the stricter test is
    # the one that catches a stale process of this same checkout.
    ours="${ours}
$(project_pids)"
  fi

  if [[ -z "${listeners}" ]]; then
    # Nothing resolvable. The port answers and the process we launched is
    # alive, which is the best evidence obtainable in this environment.
    if launcher_alive "${launched_pid}"; then
      local ident; ident="$(health_identity)"
      echo "API listener PID could not be resolved from this shell; ${ident:+process identity from /healthz: ${ident}; }launched PID ${launched_pid} is alive."
      return 0
    fi
    echo "ERROR: port ${PORT} answers /healthz but the process we launched (PID ${launched_pid}) is gone" >&2
    echo "       and no owning PID is visible. Another process is serving this port." >&2
    return 1
  fi

  # The launcher must still be alive. Without this, a `dotnet run` that died
  # on "address already in use" while a leftover backend of THIS checkout kept
  # answering /healthz would be accepted through the project_pids fallback
  # below, which is the original bug wearing a different hat.
  if ! launcher_alive "${launched_pid}"; then
    echo "ERROR: the process we launched (PID ${launched_pid}) is gone, but port ${PORT}" >&2
    echo "       still answers /healthz. Another process is serving it:" >&2
    local dead_pid
    for dead_pid in ${listeners}; do
      echo "       PID ${dead_pid}: $(proc_label "${dead_pid}")" >&2
    done
    echo "       Last lines of ${LOG_ERR}:" >&2
    tail -n 20 "${LOG_ERR}" 2>/dev/null | sed 's/^/       /' >&2
    rm -f "${PID_FILE}"
    return 1
  fi

  local pid
  for pid in ${listeners}; do
    local match=0 candidate
    for candidate in ${ours}; do
      [[ "${pid}" == "${candidate}" ]] && match=1
    done
    if [[ "${match}" -eq 0 ]]; then
      echo "ERROR: port ${PORT} is served by PID ${pid} ($(proc_label "${pid}"))," >&2
      echo "       which is not the process we just launched (PID ${launched_pid}) and does not" >&2
      echo "       belong to ${SCRIPT_DIR}." >&2
      echo "       This is the hollow-restart failure: an old process kept the port, the new" >&2
      echo "       one could not bind, and /healthz answered anyway. Stopping our orphan." >&2
      kill_tree "${launched_pid}"
      rm -f "${PID_FILE}"
      return 1
    fi
  done
  # Track the process that actually owns the port: `dotnet run` execs a child,
  # and that child is what a later stop has to target.
  local owner; owner="$(printf '%s\n' "${listeners}" | head -n1)"
  echo "${owner}" > "${PID_FILE}"
  echo "API listener PID: ${owner}"
  return 0
}

# Restart is the command that lied loudest, so it asserts the outcome twice:
# stop must verify the port is free, and the process answering afterwards must
# have a different identity than the one answering before.
cmd_restart() {
  # Before touching a running backend: if policy would refuse the start, this
  # command must not stop anything either.
  assert_boot_policy || return 1

  refresh_process_table
  local before_identity before_pids before_tokens=""
  before_identity="$(health_identity)"
  before_pids="$(remaining_targets)"
  local pid
  for pid in ${before_pids}; do
    before_tokens="${before_tokens}${pid}:$(proc_start_token "${pid}") "
  done
  if [[ -n "${before_identity}${before_tokens}" ]]; then
    echo "Restarting. Current process identity: ${before_identity:-${before_tokens}}"
  fi

  cmd_stop || return 1
  cmd_start || return 1

  refresh_process_table
  local after_identity after_pids after_tokens=""
  after_identity="$(health_identity)"
  after_pids="$(remaining_targets)"
  for pid in ${after_pids}; do
    after_tokens="${after_tokens}${pid}:$(proc_start_token "${pid}") "
  done

  # Route 1: the backend publishes its process identity on /healthz. This is
  # the only signal that survives a pid-namespace boundary.
  if [[ -n "${before_identity}" && -n "${after_identity}" ]]; then
    if [[ "${before_identity}" == "${after_identity}" ]]; then
      echo "ERROR: restart did NOT replace the process." >&2
      echo "       /healthz still reports process identity ${after_identity}." >&2
      echo "       The old backend is still serving port ${PORT}; the restart was hollow." >&2
      return 1
    fi
    echo "Restart verified: process identity changed ${before_identity} -> ${after_identity}."
    return 0
  fi

  # Route 2: pid + start time of whatever owns the port now.
  if [[ -n "${before_tokens}" && -n "${after_tokens}" ]]; then
    if [[ "${before_tokens}" == "${after_tokens}" ]]; then
      echo "ERROR: restart did NOT replace the process." >&2
      echo "       The same PID and start time still own port ${PORT}: ${after_tokens}" >&2
      return 1
    fi
    echo "Restart verified: port owner changed [${before_tokens}] -> [${after_tokens}]."
    return 0
  fi

  if [[ -z "${before_identity}${before_tokens}" ]]; then
    echo "Restart completed. Nothing was running on port ${PORT} beforehand, so this was a start."
    return 0
  fi

  # Something was running before but its identity is not comparable here: an
  # older build without the /healthz identity headers, and no visible owning
  # PID. cmd_stop proved the port went free and cmd_start proved the process it
  # launched owns it now, so this is a successful restart with a weaker proof.
  # Say that instead of implying more.
  echo "Restart completed. Process identity could not be compared in this environment"
  echo "(no /healthz identity headers and no visible owning PID); the port was verified"
  echo "free after stop and healthy after start."
  return 0
}

print_usage() {
  cat <<EOF

  API Control (sh)

  Usage: ./api.sh <command>

  Commands:
    start     Start the API (refuses if the port is owned by a foreign process)
    stop      Stop the API and verify the port is free (non-zero if it is not)
    restart   Stop + Start, asserting the process was actually replaced
    status    Show current API status, port owners, and health

  Env:
    PORT                     port to control (pinned by checkout folder name)
    API_PORT_OVERRIDE=1      accept a PORT that differs from the pinned default
    ATP_API_STOP_TIMEOUT     seconds stop keeps trying before failing (default 20)
    ATP_API_HEALTH_TIMEOUT   seconds start waits for /healthz (default 30)

EOF
}

# Sourced as a library by tools/api-restart-selfcheck.sh: define everything,
# run nothing.
if [[ "${ATP_API_SH_LIB:-}" == "1" ]]; then
  return 0 2>/dev/null || exit 0
fi

CMD="${1:-}"
case "${CMD}" in
  start)   cmd_start ;;
  stop)    cmd_stop ;;
  restart) cmd_restart ;;
  status)  cmd_status ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) echo "Unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
