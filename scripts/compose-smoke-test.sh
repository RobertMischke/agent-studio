#!/usr/bin/env bash
# Compatibility entry point. The deployment scenario owns Compose smoke now.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${COMPOSE_SMOKE_ARTIFACTS:-${JOB_RESULTS_DIR:-$repo_root/results}/deployment-scenario-compose-smoke}"

"$repo_root/scripts/scenario.sh" --target compose --level smoke --output "$output_root"
printf '%s\n' "compose-smoke=passed" "report=$output_root/scenario-report.md"
