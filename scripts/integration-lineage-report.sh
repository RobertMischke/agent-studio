#!/usr/bin/env bash
# AGT-2688: read-only topology report for the integration lineage of a managed
# repository. Answers the two questions an operator needs after an
# "integrated locally, never published" incident:
#   1. how do main, develop and their origin counterparts relate right now, and
#   2. exactly which commits sit on local develop but not on origin/develop.
#
# The script never writes: no fetch --prune, no checkout, no push. Run it on the
# host that owns the backend checkout.
set -euo pipefail

repo="${1:-.}"
work_branch="${WORK_BRANCH:-develop}"
release_branch="${RELEASE_BRANCH:-main}"

if ! git -C "$repo" rev-parse --git-dir >/dev/null 2>&1; then
  printf 'Not a git repository: %s\n' "$repo" >&2
  exit 2
fi

tip() { git -C "$repo" rev-parse --verify --quiet "$1" || true; }

printf 'Repository: %s\n' "$(cd "$repo" && pwd)"
printf 'Work branch: %s   Release branch: %s\n\n' "$work_branch" "$release_branch"

printf 'Refs (no fetch performed; values are as last known locally)\n'
for ref in "$work_branch" "origin/$work_branch" "$release_branch" "origin/$release_branch"; do
  sha="$(tip "$ref")"
  printf '  %-24s %s\n' "$ref" "${sha:-<missing>}"
done
printf '\n'

local_work="$(tip "$work_branch")"
remote_work="$(tip "origin/$work_branch")"

if [ -z "$local_work" ] || [ -z "$remote_work" ]; then
  printf 'Cannot compare %s with origin/%s: one of them is missing.\n' "$work_branch" "$work_branch"
  exit 0
fi

ahead="$(git -C "$repo" rev-list --count "origin/$work_branch..$work_branch")"
behind="$(git -C "$repo" rev-list --count "$work_branch..origin/$work_branch")"
printf 'Divergence: local %s is %s ahead / %s behind origin/%s\n' \
  "$work_branch" "$ahead" "$behind" "$work_branch"

if [ "$ahead" -gt 0 ] && [ "$behind" -eq 0 ]; then
  printf 'Shape: fast-forwardable. A push of %s publishes the commits below.\n' "$work_branch"
elif [ "$ahead" -gt 0 ] && [ "$behind" -gt 0 ]; then
  printf 'Shape: DIVERGED. A push cannot fast-forward; converge before publishing.\n'
elif [ "$ahead" -eq 0 ] && [ "$behind" -gt 0 ]; then
  printf 'Shape: local is stale; nothing local is waiting to be published.\n'
else
  printf 'Shape: in sync.\n'
fi
printf '\n'

if [ "$ahead" -gt 0 ]; then
  printf 'Commits on local %s that origin/%s does not have:\n' "$work_branch" "$work_branch"
  git -C "$repo" log --no-merges --format='  %h  %ad  %s' --date=short \
    "origin/$work_branch..$work_branch"
  printf '\n'
  printf 'Merge commits in that range (one per integrated delivery):\n'
  git -C "$repo" log --merges --format='  %h  %ad  %s' --date=short \
    "origin/$work_branch..$work_branch"
  printf '\n'
fi

# The release-line relationship is what the develop-then-main policy consults.
if [ -n "$(tip "$release_branch")" ]; then
  if git -C "$repo" merge-base --is-ancestor "$release_branch" "$work_branch" 2>/dev/null; then
    printf 'Release lineage: %s is an ancestor of %s - the main advance is permitted.\n' \
      "$release_branch" "$work_branch"
  else
    printf 'Release lineage: %s is NOT an ancestor of %s - the main advance is blocked.\n' \
      "$release_branch" "$work_branch"
    printf 'Commits on %s that %s does not contain:\n' "$release_branch" "$work_branch"
    git -C "$repo" log --format='  %h  %ad  %s' --date=short \
      "$work_branch..$release_branch"
  fi
fi
