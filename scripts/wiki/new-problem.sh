#!/usr/bin/env bash
# Scaffold a new common-problems entry from template.
# Usage: scripts/wiki/new-problem.sh <theme> <kebab-case-slug>
#
# <theme> is one of: operations | system | quality (the common-problems library
# is thematic; operations is the default inbox for runner/pipeline/sweep issues,
# system for task-server/API/git/identity, quality for frontend/test).
# Creates docs/<theme>/common-problems/<slug>/ with the canonical 6 files.
# Fills frontmatter with sensible defaults; you edit the rest.
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <theme> <kebab-case-slug>" >&2
  echo "  theme: operations | system | quality" >&2
  exit 2
fi

theme="$1"
case "$theme" in
  operations|system|quality) ;;
  *) echo "error: theme must be operations, system, or quality (got: $theme)" >&2; exit 2 ;;
esac

slug="$2"
if ! printf '%s' "$slug" | grep -Eq '^[a-z0-9]+(-[a-z0-9]+)*$'; then
  echo "error: slug must be lowercase kebab-case (got: $slug)" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
target="$repo_root/docs/$theme/common-problems/$slug"

if [ -e "$target" ]; then
  echo "error: $target already exists" >&2
  exit 1
fi

mkdir -p "$target"
now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat >"$target/README.md" <<EOF
---
id: $slug
title: "TODO: one-line human-readable title"
status: open
first-seen: $now
last-seen: $now
severity: minor
category: misc
tags: []
affects: []
related-tasks: []
related-adrs: []
---

# $slug

**What.** TODO: one-sentence symptom description.
**Why.** TODO: best current understanding of root cause.
**Workaround.** TODO: shortest reliable mitigation today.
**Long-term.** TODO: the fix or design change that would retire this entry.
EOF

cat >"$target/occurrences.md" <<EOF
# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| $now | TODO | TODO | TODO | TODO |
EOF

cat >"$target/protocol.md" <<EOF
# Root-cause protocol

TODO: detailed analyses, reproducers, log excerpts. Cite job slugs and commit hashes.
EOF

cat >"$target/measures.md" <<EOF
# Measures

Fix attempts and their status. Status vocabulary: \`tried\`, \`applied\`, \`works\`, \`regressed\`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| TODO | TODO | TODO | TODO | TODO |
EOF

cat >"$target/ideas.md" <<EOF
# Ideas

Hypotheses, open questions, ruled-out approaches. Move into measures.md once attempted.
EOF

cat >"$target/related.md" <<EOF
# Related

Cross-references to other problems (\`[[slug]]\`), tasks, ADRs, code paths.
EOF

echo "created $target"
echo "next: edit README.md frontmatter + summary, then run scripts/wiki/regenerate-index.sh"
