#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
setup="$repo_root/deploy/windows/agent-runner-tunnel/setup-windows-tunnel.ps1"
status="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-status.ps1"
keeper_registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1"
watchdog_registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1"

test -f "$setup"
test -f "$status"

# setup-windows-tunnel.ps1: self-elevation with clear consent, then the
# original register-*.ps1 scripts stay the sole registration implementation.
grep -Fq "WindowsBuiltInRole]::Administrator" "$setup"
grep -Fq -- "-Verb RunAs" "$setup"
grep -Fq "User Account Control" "$setup"
grep -Fq "consent" "$setup"
grep -Fq "ERROR_CANCELLED" "$setup"
grep -Fq "Elevation was declined" "$setup"
grep -Fq "register-tunnel-keeper.ps1" "$setup"
grep -Fq "register-tunnel-watchdog.ps1" "$setup"
grep -Fq -- "-ResultPath" "$setup"
grep -Fq "ConvertTo-Json" "$setup"
if grep -Fq -- "-RunLevel Highest" "$setup"; then
  printf 'setup-windows-tunnel.ps1 must keep the keeper/watchdog scheduled tasks unprivileged (RunLevel Limited), not run them as admin.\n' >&2
  exit 1
fi

# tunnel-status.ps1: read-only, no registration or elevation surface.
grep -Fq "Get-ScheduledTask" "$status"
grep -Fq "Get-ScheduledTaskInfo" "$status"
grep -Fq "state.json" "$status"
grep -Fq "heal_succeeded" "$status"
grep -Fq "alarmActive" "$status"
if grep -Fq "Register-ScheduledTask" "$status"; then
  printf 'tunnel-status.ps1 must stay read-only; registration belongs to setup-windows-tunnel.ps1.\n' >&2
  exit 1
fi
if grep -Fq -- "-Verb RunAs" "$status"; then
  printf 'tunnel-status.ps1 must not require elevation.\n' >&2
  exit 1
fi

test -f "$keeper_registration"
test -f "$watchdog_registration"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -Command \
    "[void][System.Management.Automation.Language.Parser]::ParseFile('$setup',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$status',[ref]\$null,[ref]\$null)"
fi

printf 'Windows tunnel setup script self-elevates with explicit consent and delegates to the existing register scripts; the status script stays read-only.\n'
