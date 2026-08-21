#!/usr/bin/env bash
# Windows-side functional watchdog for the reverse Task Server tunnel.
#
# This process is intentionally separate from AgentRunner-TunnelKeeper. It is
# registered as an S4U Scheduled Task so it survives interactive sessions. The
# probe is executed through the ordinary SSH control path and asks the runner
# host to curl its own loopback listener.

set -u

ssh_target="agent-runner"
remote_port="15031"
keeper_task="AgentRunner-TunnelKeeper"
probe_interval_seconds="60"
failure_threshold="2"
verify_attempts="6"
verify_interval_seconds="5"
max_cycles="0"
devspace_dir=""
operator_alarm_path=""
ssh_executable="${TUNNEL_WATCHDOG_SSH:-ssh}"
powershell_executable="${TUNNEL_WATCHDOG_POWERSHELL:-powershell.exe}"

usage() {
  cat <<'EOF'
Usage: tunnel-watchdog.sh [options]

  --devspace PATH                 Parent directory of the dev and stable checkouts
  --ssh-target TARGET             SSH alias for the runner host (default: agent-runner)
  --remote-port PORT              Runner-side reverse-forward port (default: 15031)
  --keeper-task NAME              Scheduled Task to restart (default: AgentRunner-TunnelKeeper)
  --operator-alarm PATH           Existing operator-alarm append-only channel
  --probe-interval-seconds N      Probe cadence (default: 60)
  --failure-threshold N           Consecutive failures before healing (default: 2)
  --verify-attempts N             Post-restart health attempts (default: 6)
  --verify-interval-seconds N     Seconds between verification attempts (default: 5)

Test-only boundary overrides:
  --max-cycles N                  Stop after N probes; 0 runs indefinitely
  --ssh-executable PATH           Override ssh
  --powershell-executable PATH    Override powershell.exe
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --devspace) devspace_dir=${2-}; shift 2 ;;
    --ssh-target) ssh_target=${2-}; shift 2 ;;
    --remote-port) remote_port=${2-}; shift 2 ;;
    --keeper-task) keeper_task=${2-}; shift 2 ;;
    --operator-alarm) operator_alarm_path=${2-}; shift 2 ;;
    --probe-interval-seconds) probe_interval_seconds=${2-}; shift 2 ;;
    --failure-threshold) failure_threshold=${2-}; shift 2 ;;
    --verify-attempts) verify_attempts=${2-}; shift 2 ;;
    --verify-interval-seconds) verify_interval_seconds=${2-}; shift 2 ;;
    --max-cycles) max_cycles=${2-}; shift 2 ;;
    --ssh-executable) ssh_executable=${2-}; shift 2 ;;
    --powershell-executable) powershell_executable=${2-}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'tunnel-watchdog: unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$remote_port:$probe_interval_seconds:$failure_threshold:$verify_attempts:$verify_interval_seconds:$max_cycles" in
  *[!0-9:]*) printf 'tunnel-watchdog: numeric options must contain non-negative integers\n' >&2; exit 2 ;;
esac
if [ "$remote_port" -lt 1 ] || [ "$remote_port" -gt 65535 ] ||
   [ "$probe_interval_seconds" -lt 1 ] || [ "$failure_threshold" -lt 1 ] ||
   [ "$verify_attempts" -lt 1 ] || [ "$verify_interval_seconds" -lt 1 ]; then
  printf 'tunnel-watchdog: port, intervals, thresholds, and attempts must be positive\n' >&2
  exit 2
fi
case "$keeper_task" in
  *[!A-Za-z0-9._-]*|'') printf 'tunnel-watchdog: keeper task name contains unsupported characters\n' >&2; exit 2 ;;
esac

script_dir=$(cd "$(dirname "$0")" && pwd)
if [ -z "$devspace_dir" ]; then
  repository_root=$(cd "$script_dir/../../.." && pwd)
  devspace_dir=$(cd "$repository_root/.." && pwd)
fi

log_path="$devspace_dir/.tunnel-watchdog.log"
state_dir="$devspace_dir/.tunnel-watchdog-state"
status_path="$state_dir/status.json"
lock_dir="$state_dir/lock"
operator_alarm_path=${operator_alarm_path:-"$devspace_dir/.operator-alarm.log"}
health_url="http://127.0.0.1:$remote_port/healthz"

mkdir -p "$state_dir"
mkdir -p "$(dirname "$operator_alarm_path")"
if ! mkdir "$lock_dir" 2>/dev/null; then
  # IgnoreNew is also configured on the Scheduled Task. This filesystem lock
  # protects manual invocations and an overlapping task-registration update.
  existing_pid=""
  if [ -r "$lock_dir/pid" ]; then
    existing_pid=$(cat "$lock_dir/pid")
  fi
  if [ -n "$existing_pid" ] && kill -0 "$existing_pid" 2>/dev/null; then
    exit 0
  fi
  rm -f "$lock_dir/pid"
  rmdir "$lock_dir" 2>/dev/null || {
    printf 'tunnel-watchdog: stale lock could not be removed: %s\n' "$lock_dir" >&2
    exit 2
  }
  mkdir "$lock_dir"
fi
printf '%s\n' "$$" > "$lock_dir/pid"
trap 'rm -f "$lock_dir/pid"; rmdir "$lock_dir" 2>/dev/null || true' EXIT INT TERM

iso_now() {
  date -u +%Y-%m-%dT%H:%M:%SZ
}

journal() {
  printf '%s %s\n' "$(iso_now)" "$*" >> "$log_path"
}

# Renders an optional timestamp as a JSON string, or the bare token `null`
# when unset. The reader on the other end (TunnelWatchdogStatus in
# backend/Features/TunnelSupervision/TunnelSupervisionStatus.cs) types these
# fields DateTime? - an empty string is not a valid DateTime and would fail
# deserialization, which is the common case for lastHealAt on a tunnel that
# has never needed healing.
json_timestamp_or_null() {
  if [ -z "$1" ]; then printf 'null'; else printf '"%s"' "$1"; fi
}

# Machine-readable snapshot read by the product's visibility surface (Studio
# admin UI). A plain journal line is enough for a human tailing the log; the
# Task Server needs one atomic-write file to poll instead of parsing history.
write_status() {
  probe_at=$1
  probe_result=$2
  temp_path="$status_path.tmp"
  cat > "$temp_path" <<STATUS
{
  "schemaVersion": 1,
  "generatedAt": "$(iso_now)",
  "sshTarget": "$ssh_target",
  "remotePort": $remote_port,
  "lastProbeAt": $(json_timestamp_or_null "$probe_at"),
  "lastProbeResult": "$probe_result",
  "consecutiveProbeFailures": $probe_failures,
  "lastHealAt": $(json_timestamp_or_null "${last_heal_at:-}"),
  "lastHealResult": "${last_heal_result:-}",
  "consecutiveHealFailures": $heal_failures
}
STATUS
  mv -f "$temp_path" "$status_path"
}

alarm() {
  alarm_line="$(iso_now) source=tunnel-watchdog severity=alarm target=$ssh_target port=$remote_port message=heal_failed_twice"
  printf '%s\n' "$alarm_line" >> "$operator_alarm_path"
  journal "event=operator_alarm heal_failures=$heal_failures channel=$operator_alarm_path"
}

probe_route() {
  "$ssh_executable" -T -o BatchMode=yes -o ConnectTimeout=10 "$ssh_target" \
    "curl -sf --max-time 6 '$health_url' >/dev/null" >/dev/null 2>&1
}

kill_remote_listener() {
  remote_cleanup=$(cat <<'EOF'
port=__PORT__
endpoint="127.0.0.1:$port"
pids=""
if command -v ss >/dev/null 2>&1; then
  pids=$(ss -H -ltnp "sport = :$port" 2>/dev/null |
    awk -v endpoint="$endpoint" '$4 == endpoint {
      line=$0
      while (match(line, /pid=[0-9]+/)) {
        print substr(line, RSTART + 4, RLENGTH - 4)
        line=substr(line, RSTART + RLENGTH)
      }
    }' | sort -u)
fi
if [ -z "$pids" ] && command -v lsof >/dev/null 2>&1; then
  pids=$(lsof -nP -t -iTCP@127.0.0.1:"$port" -sTCP:LISTEN 2>/dev/null | sort -u)
fi
if [ -z "$pids" ]; then
  printf 'no-listener\n'
  exit 0
fi
kill $pids 2>/dev/null || true
sleep 2
for pid in $pids; do
  if kill -0 "$pid" 2>/dev/null; then
    kill -KILL "$pid" 2>/dev/null || true
  fi
done
printf 'stopped-pids=%s\n' "$(printf '%s' "$pids" | tr '\n' ',')"
EOF
)
  remote_cleanup=${remote_cleanup//__PORT__/$remote_port}
  "$ssh_executable" -T -o BatchMode=yes -o ConnectTimeout=10 \
    "$ssh_target" "$remote_cleanup" 2>&1
}

restart_keeper_task() {
  "$powershell_executable" -NoProfile -NonInteractive -Command \
    "\$ErrorActionPreference='Stop'; Stop-ScheduledTask -TaskName '$keeper_task' -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-ScheduledTask -TaskName '$keeper_task' -ErrorAction Stop" \
    >/dev/null 2>&1
}

verify_route() {
  attempt=1
  while [ "$attempt" -le "$verify_attempts" ]; do
    if probe_route; then
      return 0
    fi
    if [ "$attempt" -lt "$verify_attempts" ]; then
      sleep "$verify_interval_seconds"
    fi
    attempt=$((attempt + 1))
  done
  return 1
}

heal_route() {
  journal "event=heal_started consecutive_probe_failures=$probe_failures"
  cleanup_output=$(kill_remote_listener)
  cleanup_rc=$?
  cleanup_output=$(printf '%s' "$cleanup_output" | tr '\r\n' ' ' | sed 's/[[:space:]][[:space:]]*/_/g')
  journal "event=remote_listener_cleanup result=$cleanup_rc detail=${cleanup_output:-none}"

  restart_keeper_task
  restart_rc=$?
  journal "event=keeper_restart result=$restart_rc task=$keeper_task"

  if [ "$cleanup_rc" -eq 0 ] && [ "$restart_rc" -eq 0 ] && verify_route; then
    journal "event=heal_succeeded health_url=$health_url"
    last_heal_at=$(iso_now)
    last_heal_result="succeeded"
    return 0
  fi

  journal "event=heal_failed cleanup_result=$cleanup_rc restart_result=$restart_rc health_url=$health_url"
  last_heal_at=$(iso_now)
  last_heal_result="failed"
  return 1
}

probe_failures=0
heal_failures=0
cycles=0
last_heal_at=""
last_heal_result=""
journal "event=watchdog_started interval_seconds=$probe_interval_seconds threshold=$failure_threshold target=$ssh_target port=$remote_port"
write_status "" "starting"

while :; do
  cycle_started_epoch=$(date +%s)
  cycles=$((cycles + 1))
  probed_at=$(iso_now)
  if probe_route; then
    if [ "$probe_failures" -gt 0 ] || [ "$heal_failures" -gt 0 ]; then
      journal "event=route_recovered_without_heal prior_probe_failures=$probe_failures prior_heal_failures=$heal_failures"
    fi
    probe_failures=0
    heal_failures=0
    write_status "$probed_at" "ok"
  else
    probe_failures=$((probe_failures + 1))
    journal "event=probe_failed consecutive=$probe_failures threshold=$failure_threshold"
    cycle_probe_result="failed"
    if [ "$probe_failures" -ge "$failure_threshold" ]; then
      if heal_route; then
        probe_failures=0
        heal_failures=0
        # heal_route only returns success after its own verify_route() probe
        # passed, so the route is confirmed reachable again by now - report
        # that, not the stale "failed" from the probe that triggered the heal.
        cycle_probe_result="ok"
      else
        heal_failures=$((heal_failures + 1))
        journal "event=heal_failure_count consecutive=$heal_failures alarm_threshold=2"
        if [ "$heal_failures" -eq 2 ]; then
          alarm
        fi
      fi
    fi
    write_status "$probed_at" "$cycle_probe_result"
  fi

  if [ "$max_cycles" -gt 0 ] && [ "$cycles" -ge "$max_cycles" ]; then
    journal "event=watchdog_stopped reason=max_cycles cycles=$cycles"
    exit 0
  fi
  cycle_elapsed_seconds=$(( $(date +%s) - cycle_started_epoch ))
  cycle_sleep_seconds=$((probe_interval_seconds - cycle_elapsed_seconds))
  if [ "$cycle_sleep_seconds" -gt 0 ]; then
    sleep "$cycle_sleep_seconds"
  fi
done
