#!/usr/bin/env bash
# Compatibility entry point. The compose smoke contract now lives in the
# shared deployment regression scenario.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$repo_root/scripts/scenario.sh" --target compose --level smoke "$@"
