#!/usr/bin/env bash
# Idempotent setup + reset for the click-counter scenario.
#
# Wipes the workspace's .orchestrator/jobs lanes and source files, then
# re-copies the pristine job templates from this folder back into
# 2-ready/. Safe to run any number of times. Does NOT touch your
# backend/appsettings.Local.json (the watch-path entry is one-time setup;
# see README.md).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
TEMPLATES_DIR="${SCRIPT_DIR}/jobs"
DEVSPACE_DIR="$(cd "${SCRIPT_DIR}/../../.." && pwd)"
WORKSPACE_DIR="${DEVSPACE_DIR}/scenario-click-counter/workspace"
JOBS_DIR="${WORKSPACE_DIR}/.orchestrator/jobs"

echo "[scenario] resetting click-counter scenario"
echo "[scenario]   templates: ${TEMPLATES_DIR}"
echo "[scenario]   workspace: ${WORKSPACE_DIR}"

# Wipe the workspace folder entirely. The runtime artifacts (cli-output.log,
# session-events.jsonl, status.md, the scaffolded index.html / style.css /
# script.js / README.md) all live in here; a clean wipe is the simplest way
# to guarantee a known starting state.
if [[ -d "${WORKSPACE_DIR}" ]]; then
  rm -rf "${WORKSPACE_DIR}"
fi
mkdir -p "${JOBS_DIR}"

# Create the six state lanes the scanner expects. Empty ones are fine.
for state in 1-preparation 2-ready 3-progress 4-review 5-completed 6-archive; do
  mkdir -p "${JOBS_DIR}/${state}"
done

# Copy each job template into 2-ready/ so the auto-pickup loop will run them
# in order (sorted by job.json `order`).
for job_dir in "${TEMPLATES_DIR}"/*/; do
  job_name="$(basename "${job_dir}")"
  cp -R "${job_dir}" "${JOBS_DIR}/2-ready/${job_name}"
done

# Optional README at the workspace root so a curious user opening the folder
# sees what they're looking at.
cat > "${WORKSPACE_DIR}/README.md" <<'EOF'
# Click Counter scenario workspace

This folder is the watched project for the click-counter scenario.

The agent populates `index.html`, `style.css`, `script.js`, and a sibling
`README.md` here as it works through the four queued tasks. Once the chain
is done, open `index.html` in a browser to see the result.

To reset: run `tools/scenario-click-counter/reset.sh` from the dev repo.
EOF

echo
echo "[scenario] done. Jobs queued in ${JOBS_DIR}/2-ready/:"
ls -1 "${JOBS_DIR}/2-ready/"
echo
echo "[scenario] one-time setup: add this entry to backend/appsettings.Local.json under \"WatchPaths\":"
echo

# Convert /c/foo/bar (Git Bash) or /cygdrive/c/... back to C:\\foo\\bar so the
# JSON snippet works on Windows. On non-Windows we just print as-is.
WIN_PATH="${WORKSPACE_DIR}"
if [[ "${WORKSPACE_DIR}" =~ ^/([a-zA-Z])/(.*) ]]; then
  WIN_PATH="${BASH_REMATCH[1]^^}:\\\\${BASH_REMATCH[2]//\//\\\\}"
elif [[ "${WORKSPACE_DIR}" =~ ^/cygdrive/([a-zA-Z])/(.*) ]]; then
  WIN_PATH="${BASH_REMATCH[1]^^}:\\\\${BASH_REMATCH[2]//\//\\\\}"
fi
echo "    { \"Name\": \"Click Counter\", \"RootPath\": \"${WIN_PATH}\" }"
echo
echo "[scenario] then restart the API (./api.sh restart) and switch the runner to auto-continuous in the UI."
