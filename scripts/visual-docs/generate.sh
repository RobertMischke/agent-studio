#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo "Visual documentation uses the pinned presentation pipeline"
npm --prefix "${repo_root}/frontend" run docs:presentation
