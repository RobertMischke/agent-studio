#!/usr/bin/env bash
# Self-heal half-installed npm shims for the claude / gemini CLIs on Windows.
#
# Background: npm's atomic-rename pattern (write .name-<random>, then rename to
# <name>) fails on Windows when the target file is locked, leaving orphans like
# `.claude-2shlnT4k`, `.claude.cmd-A8DH7lDq`, `.claude.ps1-Phb6s52t`. Same shape
# for gemini. The Anthropic claude-code postinstall additionally swaps a
# 500-byte stub for the real ~254 MB binary; an interrupt mid-postinstall
# leaves the stub in place AND can rename the source binary to
# `claude.exe.old.<timestamp>` inside the platform package. This script repairs
# all of those shapes and runs `claude --version` as a smoke test.
#
# Idempotent: silent when nothing is wrong, loud only when it fixes something.
# Exit 0 on success or no-op; exit 1 only when claude is still broken after the
# repair pass.
#
# Registered in docs/system/contracts/loop-inventory.md as a self-heal command id and called
# from agent-taskboard-devspace/start.sh before the backend boots, so a broken
# CLI cannot drain the Ready lane through silent pickup failures.

set -uo pipefail

# Resolve the npm global bin directory. APPDATA on Windows is a Windows-style
# path that git-bash translates; fall back to $HOME for portability.
NPM_BIN="${APPDATA:-$HOME/AppData/Roaming}/npm"
if [[ ! -d "$NPM_BIN" ]] && [[ -n "${USERPROFILE:-}" ]]; then
  NPM_BIN="$(cygpath -u "$USERPROFILE/AppData/Roaming/npm" 2>/dev/null || echo "$HOME/AppData/Roaming/npm")"
fi

if [[ ! -d "$NPM_BIN" ]]; then
  echo "[check-cli-shims] npm global bin not found at '$NPM_BIN' — skipping (no global install)"
  exit 0
fi

cd "$NPM_BIN"

healed=0

# 1. Rename dot-prefix orphan shims back to their canonical names.
#    Pattern: .claude-<random>, .claude.cmd-<random>, .claude.ps1-<random>;
#    same for gemini.
for cli in claude gemini; do
  for ext in "" ".cmd" ".ps1"; do
    target="${cli}${ext}"
    pattern=".${cli}${ext}-"
    # Use compgen so that a non-matching glob expands to nothing instead of
    # leaving the literal pattern.
    while IFS= read -r orphan; do
      [[ -n "$orphan" ]] || continue
      [[ -e "$orphan" ]] || continue
      if [[ -e "$target" ]]; then
        rm -f -- "$orphan"
        echo "[check-cli-shims] dropped redundant orphan '$orphan' (canonical '$target' already present)"
      else
        mv -- "$orphan" "$target"
        echo "[check-cli-shims] renamed orphan shim: $orphan -> $target"
      fi
      healed=1
    done < <(compgen -G "${pattern}*" 2>/dev/null || true)
  done
done

# 2. Restore the platform binary if a previous postinstall left it as
#    claude.exe.old.<timestamp> and the canonical claude.exe is missing.
PLAT_DIR="$NPM_BIN/node_modules/@anthropic-ai/claude-code-win32-x64"
if [[ -d "$PLAT_DIR" ]] && [[ ! -f "$PLAT_DIR/claude.exe" ]]; then
  newest_old=""
  while IFS= read -r f; do
    newest_old="$f"
    break
  done < <(ls -t "$PLAT_DIR"/claude.exe.old.* 2>/dev/null || true)
  if [[ -n "$newest_old" ]] && [[ -f "$newest_old" ]]; then
    mv -- "$newest_old" "$PLAT_DIR/claude.exe"
    echo "[check-cli-shims] restored platform binary: $(basename "$newest_old") -> claude.exe"
    healed=1
  fi
fi

# 3. Repair the wrapper bin/claude.exe.
#    Three failure shapes observed on Windows after an interrupted npm install:
#      (a) the canonical claude.exe is present but truncated to a ~500-byte stub
#          (the npm postinstall got mid-way through the platform-binary swap),
#      (b) the canonical claude.exe is missing AND a sibling
#          claude.exe.old.<timestamp> is left behind (rename half-completed),
#      (c) the canonical claude.exe is missing and no .old.<ts> sibling either
#          (full delete by the installer's preinstall step before crash).
#    Shape (a) heals by re-running the wrapper postinstall.
#    Shape (b) heals by renaming the .old.<ts> back to claude.exe; no postinstall
#    required because the file content is the previously-correct binary.
#    Shape (c) needs postinstall too.
WRAP_DIR="$NPM_BIN/node_modules/@anthropic-ai/claude-code"
WRAP_BIN="$WRAP_DIR/bin/claude.exe"
if [[ -d "$WRAP_DIR" ]]; then
  # Shape (b): missing canonical, .old.<ts> sibling present → rename back.
  if [[ ! -f "$WRAP_BIN" ]]; then
    newest_wrap_old=""
    while IFS= read -r f; do
      newest_wrap_old="$f"
      break
    done < <(ls -t "$WRAP_DIR/bin/"claude.exe.old.* 2>/dev/null || true)
    if [[ -n "$newest_wrap_old" ]] && [[ -f "$newest_wrap_old" ]]; then
      mv -- "$newest_wrap_old" "$WRAP_BIN"
      echo "[check-cli-shims] restored wrapper binary: $(basename "$newest_wrap_old") -> claude.exe"
      healed=1
    fi
  fi
  # Shape (a) and shape (c): present-but-stub OR still missing → postinstall.
  needs_postinstall=0
  if [[ ! -f "$WRAP_BIN" ]]; then
    needs_postinstall=1
    echo "[check-cli-shims] wrapper bin/claude.exe still missing after .old fallback; running postinstall..."
  else
    size="$(wc -c < "$WRAP_BIN" | tr -d ' ')"
    if [[ "$size" -lt 4096 ]]; then
      needs_postinstall=1
      echo "[check-cli-shims] stub binary detected at claude-code/bin/claude.exe ($size bytes), running postinstall..."
    fi
  fi
  if [[ "$needs_postinstall" -eq 1 ]]; then
    if (cd "$WRAP_DIR" && node install.cjs); then
      echo "[check-cli-shims] postinstall completed."
    else
      echo "[check-cli-shims] WARN: postinstall returned non-zero. Smoke test below will be the verdict."
    fi
    healed=1
  fi
fi

# 4. Remove orphan staging directories under @anthropic-ai/.
#    Pattern: .<pkgname>-<random>/ left behind by interrupted npm installs.
ANTHROPIC_DIR="$NPM_BIN/node_modules/@anthropic-ai"
if [[ -d "$ANTHROPIC_DIR" ]]; then
  while IFS= read -r d; do
    [[ -n "$d" ]] || continue
    [[ -d "$d" ]] || continue
    rm -rf "$d"
    echo "[check-cli-shims] removed staging orphan: $d"
    healed=1
  done < <(compgen -G "$ANTHROPIC_DIR/.*-*" 2>/dev/null || true)
fi

# 5. Smoke test. Prefer the npm shim if it's there and runs; otherwise
#    accept any claude on PATH (e.g. Anthropic's native installer at
#    ~/.local/bin/claude.exe, installed via install.sh / the Windows
#    installer — completely independent of npm). Two install methods
#    coexisting on the same machine is a supported reality; the boot
#    check should only fail when neither produces a working `claude`.
SHIM_CMD="$NPM_BIN/claude.cmd"
shim_ok=0
if [[ -f "$SHIM_CMD" ]] && "$SHIM_CMD" --version >/dev/null 2>&1; then
  shim_ok=1
fi

if [[ "$shim_ok" -eq 0 ]]; then
  # Look for an alternative claude on PATH that is NOT the npm shim
  # itself (skipping it avoids looping back to the broken file). Use
  # `where` on Windows (lists all PATH hits); fall back to POSIX
  # `command -v` elsewhere.
  NPM_BIN_UNIX="$(cygpath -u "$NPM_BIN" 2>/dev/null || echo "$NPM_BIN")"
  alt_claude=""
  while IFS= read -r p; do
    [[ -n "$p" ]] || continue
    abs="$(cygpath -u "$p" 2>/dev/null || echo "$p")"
    case "$abs" in
      "$NPM_BIN_UNIX"/*|"$NPM_BIN"/*) continue ;;
    esac
    [[ -f "$abs" ]] || continue
    alt_claude="$abs"
    break
  done < <(
    if command -v where >/dev/null 2>&1; then
      where claude.exe 2>/dev/null
      where claude 2>/dev/null
    else
      command -v claude 2>/dev/null
    fi
  )

  if [[ -n "$alt_claude" ]] && "$alt_claude" --version >/dev/null 2>&1; then
    ver="$("$alt_claude" --version 2>/dev/null | tr -d '\r' | head -1)"
    # A6 (2026-05-22): the fallback-to-PATH-claude message printed every
    # boot, even when the install hadn't changed. Quiet it down: store
    # the resolved path + version under $TMPDIR (or $NPM_BIN if writable)
    # and only re-print when the fallback target changes from the last
    # boot. Override with ATP_CLI_SHIM_VERBOSE=1 to always print.
    cache_dir="${TMPDIR:-$NPM_BIN}"
    cache_file="$cache_dir/.atp-claude-fallback"
    cache_line="$alt_claude|$ver"
    prev_line=""
    [[ -f "$cache_file" ]] && prev_line="$(cat "$cache_file" 2>/dev/null | head -1)"
    if [[ "${ATP_CLI_SHIM_VERBOSE:-0}" == "1" || "$cache_line" != "$prev_line" ]]; then
      echo "[check-cli-shims] npm shim missing/broken at $SHIM_CMD; using PATH-resolved claude at $alt_claude ($ver)."
      echo "$cache_line" > "$cache_file" 2>/dev/null || true
    fi
    exit 0
  fi

  if [[ ! -f "$SHIM_CMD" ]]; then
    echo "[check-cli-shims] ERROR: $SHIM_CMD still missing after repair pass and no working claude on PATH." >&2
  else
    echo "[check-cli-shims] ERROR: 'claude --version' failed via $SHIM_CMD and no working claude on PATH." >&2
  fi
  exit 1
fi

if [[ "$healed" -ne 0 ]]; then
  echo "[check-cli-shims] healed and verified ($("$SHIM_CMD" --version 2>/dev/null | tr -d '\r' | head -1))."
fi
exit 0
