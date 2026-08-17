#!/usr/bin/env bash

set -u

demo_log() { printf '[demo-runtime] %s\n' "$*"; }
demo_error() { printf '[demo-runtime] ERROR: %s\n' "$*" >&2; }

demo_require_file() {
  local path="$1" label="$2"
  [[ -f "$path" ]] || { demo_error "$label does not exist: $path"; return 1; }
}

demo_require_hook() {
  local path="$1" label="$2"
  [[ -x "$path" ]] || { demo_error "$label must be an executable hook: $path"; return 1; }
}

demo_validate_runtime_root() {
  local path="$1"
  [[ -n "$path" && "$path" = /* ]] || { demo_error "runtime root must be an absolute path"; return 1; }
  case "$path" in
    /|/home|/root|/opt|/srv|/var|/tmp) demo_error "runtime root is too broad: $path"; return 1 ;;
  esac
}

demo_acquire_lock() {
  local runtime_root="$1"
  DEMO_RUNTIME_LOCK="$runtime_root/.replacement.lock"
  if ! mkdir "$DEMO_RUNTIME_LOCK" 2>/dev/null; then
    demo_error "another replacement or rollback owns $DEMO_RUNTIME_LOCK"
    return 1
  fi
}

demo_release_lock() {
  if [[ -n "${DEMO_RUNTIME_LOCK:-}" && -d "$DEMO_RUNTIME_LOCK" ]]; then
    rmdir "$DEMO_RUNTIME_LOCK" 2>/dev/null || true
  fi
}

demo_link_target() {
  local path="$1"
  [[ -L "$path" ]] && readlink -f "$path" || true
}

demo_atomic_link() {
  local target="$1" link="$2"
  local pending="${link}.new.$$"
  ln -s "$target" "$pending"
  mv -Tf "$pending" "$link"
}

demo_prune_inactive_releases() {
  local runtime_root="$1" releases_root current previous entry resolved
  releases_root="$(readlink -m -- "$runtime_root/releases")" || return 1
  current="$(demo_link_target "$runtime_root/current")"
  previous="$(demo_link_target "$runtime_root/previous")"
  while IFS= read -r -d '' entry; do
    resolved="$(readlink -f -- "$entry")" || continue
    [[ "$resolved" == "$releases_root/"* ]] || { demo_error "refusing to prune release outside $releases_root: $resolved"; return 1; }
    [[ "$resolved" != "$current" && "$resolved" != "$previous" ]] || continue
    [[ -f "$resolved/release/demo-release-manifest.json" ]] || continue
    chmod -R u+w "$resolved" 2>/dev/null || true
    rm -rf -- "$resolved"
    demo_log "discarded inactive runtime drift: $(basename "$resolved")"
  done < <(find "$releases_root" -mindepth 1 -maxdepth 1 -type d -print0)
}
