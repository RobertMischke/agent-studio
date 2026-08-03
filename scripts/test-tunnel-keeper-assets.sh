#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keeper="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1"
registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1"

test -f "$keeper"
test -f "$registration"

grep -Fq "AGENT_TASK_SERVER_ROUTE_OK" "$keeper"
grep -Fq "curl --fail --silent --show-error --max-time 10" "$keeper"
grep -Fq "Get-CimInstance Win32_Process" "$keeper"
grep -Fq "Stop-MatchingForwards" "$keeper"
grep -Fq "Test-NativeArgument" "$keeper"
grep -Fq "[Regex]::Escape(\$forward)" "$keeper"
grep -Fq "ExitOnForwardFailure=yes" "$keeper"
grep -Fq "ServerAliveInterval=30" "$keeper"
grep -Fq -- "-MultipleInstances IgnoreNew" "$registration"
grep -Fq -- "-RepetitionInterval" "$registration"
grep -Fq "Register-ScheduledTask" "$registration"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -Command \
    "[void][System.Management.Automation.Language.Parser]::ParseFile('$keeper',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$registration',[ref]\$null,[ref]\$null)"
fi

printf 'Tunnel keeper assets contain the functional probe, targeted cleanup, supervised SSH options, and five-minute registration contract.\n'
