#!/usr/bin/env bash
# Compatibility entry point. The onboarding curls now belong to the shared
# deployment scenario so Compose has one acceptance definition.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$repo_root/scripts/scenario.sh" \
  --target compose \
  --level smoke \
  --results-dir "${JOB_RESULTS_DIR:-$repo_root/results}"
