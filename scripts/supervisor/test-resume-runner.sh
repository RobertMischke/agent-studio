#!/usr/bin/env sh
# Lightweight test harness for resume-runner.sh.
#
# Spins up a tiny Python stub backend (no external deps) that simulates
# three things in sequence:
#   1. /healthz returns 503 for the first N polls, then 200, exercising
#      the wait-for-health loop (no fixed sleep allowed).
#   2. /api/clients/register accepts a JSON body with displayName and
#      returns a synthesised id, exercising the auto-register branch when
#      ATP_CLIENT_ID is empty.
#   3. /api/runner/<project>/mode (PUT) responds 200 but the project's
#      mode in /api/runner/status only flips to auto-continuous on the
#      Kth attempt, exercising the verification + retry loop.
#
# The test then runs resume-runner.sh against the stub and asserts:
#   - resume-runner.sh exits 0 (verified).
#   - The stub recorded N+1 healthz probes (last being 200).
#   - The stub recorded at least 1 PUT and saw the eventual mode flip.
#
# Usage:
#   ./scripts/supervisor/test-resume-runner.sh
#
# Skips with code 0 if Python 3 is not on PATH.

set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
HELPER="$SCRIPT_DIR/resume-runner.sh"

if [ ! -x "$HELPER" ]; then
  echo "[test-resume-runner] ERROR: $HELPER missing or not executable" >&2
  exit 2
fi

PYTHON=""
# Some Windows installs ship a `python` shim that just prints
# "Python was not found" and exits non-zero; require a real version probe.
for c in python3 py python; do
  if command -v "$c" >/dev/null 2>&1; then
    if "$c" -c 'import sys; sys.exit(0 if sys.version_info[0]>=3 else 1)' >/dev/null 2>&1; then
      PYTHON="$c"; break
    fi
  fi
done
if [ -z "$PYTHON" ]; then
  echo "[test-resume-runner] SKIP: python (3.x) is not on PATH"
  exit 0
fi

# Pick a stub port. 0.0.0.0:0 binding via Python returns the assigned
# port over a tiny handshake file we read after the stub starts.
TMP_DIR="$(mktemp -d 2>/dev/null || mktemp -d -t resume-runner-test)"
trap 'kill "${STUB_PID:-0}" 2>/dev/null; rm -rf "$TMP_DIR"' EXIT INT TERM
PORT_FILE="$TMP_DIR/port"
STATE_FILE="$TMP_DIR/state.json"

cat >"$TMP_DIR/stub.py" <<'PY'
import http.server, json, os, sys, threading, time
from urllib.parse import urlparse

PORT_FILE = sys.argv[1]
STATE_FILE = sys.argv[2]

# State: how many healthz polls we have seen, how many resume PUTs, and
# the project mode that /api/runner/status reports back. The project mode
# starts as "paused" and only flips on the third PUT.
state = {
    "healthz_polls": 0,
    "puts": 0,
    "mode": "paused",
    "registered": [],
}

def write_state():
    with open(STATE_FILE, "w") as f:
        json.dump(state, f)

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a, **k):
        pass

    def _send_json(self, status, body):
        raw = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/healthz":
            state["healthz_polls"] += 1
            write_state()
            # 503 for the first 3 polls, then 200. Mirrors a real backend
            # that has bound the port but not yet finished startup.
            if state["healthz_polls"] < 4:
                self.send_response(503); self.end_headers(); return
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"ok")
            return
        if path == "/api/runner/status":
            self._send_json(200, {
                "projects": {
                    "agent-taskboard": {
                        "projectName": "agent-taskboard",
                        "mode": state["mode"],
                        "activeJobId": None,
                        "queuedJobIds": [],
                    }
                }
            })
            return
        self.send_response(404); self.end_headers()

    def do_POST(self):
        path = urlparse(self.path).path
        if path == "/api/clients/register":
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length) if length else b"{}"
            try:
                payload = json.loads(body)
            except Exception:
                payload = {}
            display = payload.get("displayName") or "anon"
            cid = display.lower().replace(" ", "-")
            state["registered"].append(cid)
            write_state()
            self._send_json(200, {"id": cid, "displayName": display, "kind": "service"})
            return
        self.send_response(404); self.end_headers()

    def do_PUT(self):
        path = urlparse(self.path).path
        if path == "/api/runner/agent-taskboard/mode":
            # Require X-Client-Id, mirroring the real boundary.
            cid = self.headers.get("X-Client-Id")
            if not cid:
                self._send_json(401, {"error": "client-unknown"})
                return
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length) if length else b"{}"
            try:
                payload = json.loads(body)
            except Exception:
                payload = {}
            state["puts"] += 1
            # Flip mode only on the third PUT, exercising the retry loop.
            if state["puts"] >= 3 and payload.get("mode") == "auto-continuous":
                state["mode"] = "auto-continuous"
            write_state()
            self.send_response(200); self.end_headers()
            return
        self.send_response(404); self.end_headers()

server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
with open(PORT_FILE, "w") as f:
    f.write(str(server.server_address[1]))
write_state()
server.serve_forever()
PY

"$PYTHON" "$TMP_DIR/stub.py" "$PORT_FILE" "$STATE_FILE" &
STUB_PID=$!

# Wait for the stub to write its assigned port. Bound at 5 s.
i=0
while [ ! -s "$PORT_FILE" ]; do
  i=$((i + 1))
  if [ "$i" -ge 50 ]; then
    echo "[test-resume-runner] ERROR: stub never bound a port" >&2
    exit 1
  fi
  sleep 0.1
done
PORT="$(cat "$PORT_FILE")"
API="http://127.0.0.1:$PORT"
echo "[test-resume-runner] stub up on $API"

# Run the helper against the stub. Tight backoff so the test is fast.
set +e
ATP_API="$API" \
ATP_RESUME_MAX_ATTEMPTS=10 \
ATP_RESUME_BACKOFF=1 \
ATP_HEALTHZ_TIMEOUT=10 \
"$HELPER" agent-taskboard
HELPER_RC=$?
set -e

if [ "$HELPER_RC" -ne 0 ]; then
  echo "[test-resume-runner] FAIL: resume-runner.sh exited $HELPER_RC (expected 0)" >&2
  cat "$STATE_FILE" >&2 || true
  exit 1
fi

# Assert the stub's recorded interactions look right.
cat >"$TMP_DIR/check.py" <<'PY'
import json, sys
state = json.load(open(sys.argv[1]))
errors = []
if state["healthz_polls"] < 4:
    errors.append("healthz_polls=%d < 4 (wait-for-health did not actually wait)" % state["healthz_polls"])
if state["mode"] != "auto-continuous":
    errors.append("mode=%r (resume not verified)" % state["mode"])
if state["puts"] < 3:
    errors.append("puts=%d < 3 (verification retry loop did not fire)" % state["puts"])
if not state["registered"]:
    errors.append("no identity was auto-registered")
if errors:
    print("FAIL:", *errors, sep=" ")
    sys.exit(1)
print("OK: healthz_polls=%(healthz_polls)d puts=%(puts)d mode=%(mode)s registered=%(registered)s" % state)
PY

if ! "$PYTHON" "$TMP_DIR/check.py" "$STATE_FILE"; then
  echo "[test-resume-runner] FAIL: stub state did not match expectations" >&2
  cat "$STATE_FILE" >&2 || true
  exit 1
fi

echo "[test-resume-runner] PASS"
