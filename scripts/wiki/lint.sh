#!/usr/bin/env bash
# Validate frontmatter on every docs/wiki/common-problems/<slug>/README.md.
# Exits non-zero with a per-file reason on failure. Silent on success except for a final OK line.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
root="$repo_root/docs/wiki/common-problems"

required_keys="id title status first-seen last-seen severity category tags affects related-tasks related-adrs"
allowed_status="open mitigated fixed archived"
allowed_severity="blocker major minor nuisance"
allowed_category="permission filesystem cli runner ui state-machine misc"

errors=0
checked=0

fail() {
  printf 'lint: %s: %s\n' "$1" "$2" >&2
  errors=$((errors + 1))
}

in_list() {
  needle="$1"
  shift
  for v in "$@"; do
    [ "$v" = "$needle" ] && return 0
  done
  return 1
}

# Extract a top-level scalar value from frontmatter. Lines like `key: value`.
# Returns empty if missing.
fm_scalar() {
  file="$1"
  key="$2"
  awk -v key="$key" '
    /^---[[:space:]]*$/ { fm = !fm; next }
    fm && $0 ~ "^"key":" {
      sub("^"key":[[:space:]]*", "", $0)
      sub("[[:space:]]*$", "", $0)
      print
      exit
    }
  ' "$file"
}

# Does the file have an opened-and-closed YAML frontmatter block at the top?
has_frontmatter() {
  awk '
    NR == 1 && $0 !~ /^---[[:space:]]*$/ { exit 1 }
    NR == 1 { opened = 1; next }
    opened && $0 ~ /^---[[:space:]]*$/ { closed = 1; exit 0 }
    END { exit (closed ? 0 : 1) }
  ' "$1"
}

shopt -s nullglob
for dir in "$root"/*/; do
  slug="$(basename "$dir")"
  case "$slug" in
    archive) continue ;;
  esac
  readme="$dir/README.md"
  rel="docs/wiki/common-problems/$slug/README.md"
  checked=$((checked + 1))

  if [ ! -f "$readme" ]; then
    fail "$rel" "missing README.md"
    continue
  fi

  if ! has_frontmatter "$readme"; then
    fail "$rel" "missing or malformed YAML frontmatter block"
    continue
  fi

  if grep -Eq 'TODO: one-line human-readable title|TODO: one-sentence symptom description|TODO: best current understanding|TODO: shortest reliable mitigation|TODO: the fix or design change' "$readme"; then
    fail "$rel" "README.md still contains scaffold placeholder text"
  fi

  for k in $required_keys; do
    # Special-case list-shaped keys: presence-check only.
    case "$k" in
      tags|affects|related-tasks|related-adrs)
        if ! grep -Eq "^${k}:" "$readme"; then
          fail "$rel" "missing required key: $k"
        fi
        ;;
      *)
        v="$(fm_scalar "$readme" "$k")"
        if [ -z "$v" ]; then
          fail "$rel" "missing required key: $k"
        fi
        ;;
    esac
  done

  id_v="$(fm_scalar "$readme" id)"
  if [ -n "$id_v" ] && [ "$id_v" != "$slug" ]; then
    fail "$rel" "id ($id_v) does not match folder name ($slug)"
  fi

  status_v="$(fm_scalar "$readme" status)"
  if [ -n "$status_v" ] && ! in_list "$status_v" $allowed_status; then
    fail "$rel" "status not in [$allowed_status]: $status_v"
  fi

  sev_v="$(fm_scalar "$readme" severity)"
  if [ -n "$sev_v" ] && ! in_list "$sev_v" $allowed_severity; then
    fail "$rel" "severity not in [$allowed_severity]: $sev_v"
  fi

  cat_v="$(fm_scalar "$readme" category)"
  if [ -n "$cat_v" ] && ! in_list "$cat_v" $allowed_category; then
    fail "$rel" "category not in [$allowed_category]: $cat_v"
  fi

  for f in occurrences.md protocol.md measures.md ideas.md related.md; do
    if [ ! -f "$dir/$f" ]; then
      fail "$rel" "sibling file missing: $f"
    fi
  done

  if [ -f "$dir/occurrences.md" ] && grep -Eq '\|[[:space:]]*TODO[[:space:]]*\|' "$dir/occurrences.md"; then
    fail "$rel" "occurrences.md still contains scaffold TODO row"
  fi
done

if [ "$errors" -gt 0 ]; then
  printf 'lint: %d error(s) across %d folder(s)\n' "$errors" "$checked" >&2
  exit 1
fi

printf 'lint: ok (%d folder(s))\n' "$checked"
