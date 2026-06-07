#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
pw_target="${PW_TARGET:-dev}"
pw_project="${PW_PROJECT:-chromium}"

echo "Generating visual documentation screenshots"
echo "  repo: $repo_root"
echo "  target: $pw_target"
echo "  project: $pw_project"

cd "$repo_root/frontend"
PW_TARGET="$pw_target" npx playwright test \
  e2e/visual-evidence/readme-screenshots.spec.ts \
  --project="$pw_project"

cd "$repo_root"
node scripts/visual-docs/validate.mjs

echo "Visual documentation screenshots are ready in docs/images/."
