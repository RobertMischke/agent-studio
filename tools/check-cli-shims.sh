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
# Registered in docs/loop-inventory.md as a self-heal command id and called
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

# 3. Replace the 500-byte stub at claude-code/bin/claude.exe by re-running the
#    package postinstall when the file is implausibly small.
WRAP_DIR="$NPM_BIN/node_modules/@anthropic-ai/claude-code"
if [[ -d "$WRAP_DIR" ]] && [[ -f "$WRAP_DIR/bin/claude.exe" ]]; then
  size="$(wc -c < "$WRAP_DIR/bin/claude.exe" | tr -d ' ')"
  if [[ "$size" -lt 4096 ]]; then
    echo "[check-cli-shims] stub binary detected at claude-code/bin/claude.exe ($size bytes), running postinstall..."
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

# 5. Smoke test. The shim file is what the OS actually invokes via PATH; call
#    it directly so we don't depend on PATH ordering in the calling shell.
SHIM_CMD="$NPM_BIN/claude.cmd"
if [[ ! -f "$SHIM_CMD" ]]; then
  echo "[check-cli-shims] ERROR: $SHIM_CMD still missing after repair pass." >&2
  exit 1
fi
if ! "$SHIM_CMD" --version >/dev/null 2>&1; then
  echo "[check-cli-shims] ERROR: 'claude --version' failed after repair pass." >&2
  exit 1
fi

if [[ "$healed" -ne 0 ]]; then
  echo "[check-cli-shims] healed and verified ($("$SHIM_CMD" --version 2>/dev/null | tr -d '\r' | head -1))."
fi
exit 0
