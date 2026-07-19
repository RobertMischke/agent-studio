#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
stack="${repo_root}/scripts/worktree-test-stack.sh"

cleanup() {
  bash "${stack}" down >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Resetting an isolated ADR-0056 demo workspace and generating presentation stills"
bash "${stack}" down >/dev/null 2>&1 || true
bash "${stack}" up --demo --with-frontend
eval "$(bash "${stack}" env)"

cd "${repo_root}/frontend"
PW_VISUAL_CAPTURE=marketing \
  PW_BASE_URL="${PW_BASE_URL}" \
  PW_BACKEND_URL="${PW_BACKEND_URL}" \
  npx playwright test e2e/visual-evidence/presentation-capture.spec.ts --project=chromium

echo "Presentation stills are ready in docs/assets/images/presentation/."
