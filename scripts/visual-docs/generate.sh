#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
pw_target="${PW_TARGET:-dev}"
pw_project="${PW_PROJECT:-chromium}"
pw_visual_capture="${PW_VISUAL_CAPTURE:-marketing}"

echo "Generating visual documentation screenshots"
echo "  repo: $repo_root"
echo "  target: $pw_target"
echo "  project: $pw_project"
echo "  visual capture: $pw_visual_capture"

cd "$repo_root/frontend"
PW_TARGET="$pw_target" PW_VISUAL_CAPTURE="$pw_visual_capture" npx playwright test \
  e2e/visual-evidence/readme-screenshots.spec.ts \
  --project="$pw_project"

cd "$repo_root"
node scripts/visual-docs/validate.mjs

echo "Visual documentation screenshots are ready in docs/assets/images/."
