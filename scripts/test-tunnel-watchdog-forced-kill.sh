#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
watchdog="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh"
test_root="$(mktemp -d)"

cleanup() {
  for pid_file in "$test_root"/*.pid "$test_root"/*/*.pid; do
    [ -f "$pid_file" ] || continue
    pid=$(cat "$pid_file")
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$test_root"
}
trap cleanup EXIT

fake_bin="$test_root/bin"
fake_devspace="$test_root/devspace"
fake_state="$test_root/route"
mkdir -p "$fake_bin" "$fake_devspace" "$fake_state"

cat > "$fake_state/health-server.py" <<'EOF'
import socketserver
import sys

mode = sys.argv[2]

class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        self.request.recv(4096)
        if mode == "healthy":
            self.request.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")

class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True

with Server(("127.0.0.1", int(sys.argv[1])), Handler) as server:
    server.serve_forever()
EOF

cat > "$fake_bin/ssh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
remote_command=${!#}
if [[ "$remote_command" == *"curl -sf --max-time 6"* ]]; then
  url=$(printf '%s' "$remote_command" | sed -n "s#.*'\(http://127\.0\.0\.1:[0-9][0-9]*/healthz\)'.*#\1#p")
  curl -sf --max-time 1 "$url" >/dev/null
  exit $?
fi
bash -c "$remote_command"
EOF

cat > "$fake_bin/powershell.exe" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_TUNNEL_STATE/powershell-calls.log"
if [ "${FAKE_POWERSHELL_FAIL:-0}" = "1" ]; then
  exit 1
fi
setsid python3 "$FAKE_TUNNEL_STATE/health-server.py" "$FAKE_REMOTE_PORT" healthy \
  > "$FAKE_TUNNEL_STATE/replacement.log" 2>&1 &
printf '%s\n' "$!" > "$FAKE_TUNNEL_STATE/replacement.pid"
EOF
chmod +x "$fake_bin/ssh" "$fake_bin/powershell.exe" "$watchdog"

# Begin with a real healthy socket, force-kill it, then leave a real listener
# that accepts the connection without serving HTTP. This reproduces the dead
# route plus zombie-listener shape without touching the production port.
remote_port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')
python3 "$fake_state/health-server.py" "$remote_port" healthy > "$fake_state/initial.log" 2>&1 &
initial_pid=$!
printf '%s\n' "$initial_pid" > "$test_root/initial.pid"
for _ in $(seq 1 20); do
  curl -sf --max-time 1 "http://127.0.0.1:$remote_port/healthz" >/dev/null 2>&1 && break
  sleep 0.1
done
curl -sf --max-time 1 "http://127.0.0.1:$remote_port/healthz" >/dev/null
kill -KILL "$initial_pid"
{ wait "$initial_pid" || true; } >/dev/null 2>&1
python3 "$fake_state/health-server.py" "$remote_port" zombie > "$fake_state/zombie.log" 2>&1 &
zombie_pid=$!
printf '%s\n' "$zombie_pid" > "$test_root/zombie.pid"
sleep 0.2
if curl -sf --max-time 1 "http://127.0.0.1:$remote_port/healthz" >/dev/null 2>&1; then
  printf 'Forced-kill fixture unexpectedly remained healthy.\n' >&2
  exit 1
fi
mkdir -p "$fake_devspace/.tunnel-watchdog-state/lock"
printf '99999999\n' > "$fake_devspace/.tunnel-watchdog-state/lock/pid"

started_epoch=$(date +%s)
FAKE_TUNNEL_STATE="$fake_state" FAKE_REMOTE_PORT="$remote_port" "$watchdog" \
  --devspace "$fake_devspace" \
  --ssh-target agent-runner \
  --remote-port "$remote_port" \
  --keeper-task AgentRunner-TunnelKeeper \
  --probe-interval-seconds 1 \
  --failure-threshold 2 \
  --verify-attempts 2 \
  --verify-interval-seconds 1 \
  --max-cycles 2 \
  --ssh-executable "$fake_bin/ssh" \
  --powershell-executable "$fake_bin/powershell.exe"
elapsed=$(( $(date +%s) - started_epoch ))

log="$fake_devspace/.tunnel-watchdog.log"
grep -Fq 'event=probe_failed consecutive=1 threshold=2' "$log"
grep -Fq 'event=probe_failed consecutive=2 threshold=2' "$log"
grep -Fq 'event=remote_listener_cleanup result=0 detail=stopped-pids=' "$log"
grep -Fq 'event=keeper_restart result=0 task=AgentRunner-TunnelKeeper' "$log"
grep -Fq "event=heal_succeeded health_url=http://127.0.0.1:$remote_port/healthz" "$log"
grep -Fq 'Stop-ScheduledTask' "$fake_state/powershell-calls.log"
grep -Fq 'Start-ScheduledTask' "$fake_state/powershell-calls.log"
curl -sf --max-time 1 "http://127.0.0.1:$remote_port/healthz" >/dev/null
test "$elapsed" -le 12

printf 'Isolated forced-kill watchdog harness passed in %ss. Production-equivalent detection budget: two 60s probe ticks.\n' "$elapsed"
sed -n '/event=probe_failed\|event=heal_started\|event=remote_listener_cleanup\|event=keeper_restart\|event=heal_succeeded/p' "$log"

# A continuing failure gets one operator alarm on the second failed heal. The
# failed-heal retry occurs on the next 60-second production tick.
alarm_devspace="$test_root/alarm-devspace"
alarm_state="$test_root/alarm-route"
mkdir -p "$alarm_devspace" "$alarm_state"
cp "$fake_state/health-server.py" "$alarm_state/health-server.py"
alarm_port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')
FAKE_TUNNEL_STATE="$alarm_state" FAKE_REMOTE_PORT="$alarm_port" FAKE_POWERSHELL_FAIL=1 "$watchdog" \
  --devspace "$alarm_devspace" \
  --ssh-target agent-runner \
  --remote-port "$alarm_port" \
  --keeper-task AgentRunner-TunnelKeeper \
  --probe-interval-seconds 1 \
  --failure-threshold 2 \
  --verify-attempts 1 \
  --verify-interval-seconds 1 \
  --max-cycles 3 \
  --ssh-executable "$fake_bin/ssh" \
  --powershell-executable "$fake_bin/powershell.exe"
grep -Fq 'event=heal_failure_count consecutive=2 alarm_threshold=2' "$alarm_devspace/.tunnel-watchdog.log"
test "$(grep -Fc 'source=tunnel-watchdog severity=alarm' "$alarm_devspace/.operator-alarm.log")" -eq 1
printf 'Repeated-heal-failure alarm simulation passed; exactly one operator alarm was appended.\n'
