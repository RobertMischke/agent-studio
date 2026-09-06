#!/usr/bin/env bash
# Stable cross-target entry point for the deployment regression scenario.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python_command=python3
if ! command -v "$python_command" >/dev/null 2>&1; then
    python_command=python
fi
exec "$python_command" "$repo_root/scripts/scenario.py" "$@"
