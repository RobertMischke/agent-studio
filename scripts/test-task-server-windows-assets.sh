#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
asset_root="$repo_root/deploy/windows/task-server"
library="$asset_root/task-server-lib.ps1"
start="$asset_root/start-task-server.ps1"
backup="$asset_root/backup-task-server.ps1"
registration="$asset_root/register-task-server.ps1"
backup_registration="$asset_root/register-task-server-backup.ps1"
environment_example="$asset_root/server.env.example"
profile="$repo_root/task-server/Properties/PublishProfiles/win-x64.pubxml"

test -f "$library"
test -f "$start"
test -f "$backup"
test -f "$registration"
test -f "$backup_registration"
test -f "$environment_example"
test -f "$profile"

grep -Fq "<RuntimeIdentifier>win-x64</RuntimeIdentifier>" "$profile"
grep -Fq "<SelfContained>true</SelfContained>" "$profile"
grep -Fq "<PublishSingleFile>true</PublishSingleFile>" "$profile"
grep -Fq "<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>" "$profile"

grep -Fq "Import-ServerEnvironmentFile" "$library"
grep -Fq "Resolve-TaskServerExecutable" "$library"
grep -Fq "SetEnvironmentVariable" "$library"
grep -Fq "Move-Item -LiteralPath \$temporary" "$library"
grep -Fq "task-server.exe" "$library"

grep -Fq "Threading.Mutex" "$start"
grep -Fq "RedirectStandardOutput" "$start"
grep -Fq "RedirectStandardError" "$start"
grep -Fq -- "-WindowStyle Hidden" "$start"
grep -Fq "WaitForExit" "$start"
grep -Fq "event=server_exited" "$start"
grep -Fq "backup --name" "$backup"

for script in "$registration" "$backup_registration"; do
  grep -Fq -- "-LogonType S4U" "$script"
  grep -Fq -- "-MultipleInstances IgnoreNew" "$script"
  grep -Fq "Register-ScheduledTask" "$script"
  if grep -Fq -- "-AtLogOn" "$script"; then
    printf 'Task Server assets must register services, never session-bound logon tasks.\n' >&2
    exit 1
  fi
done
grep -Fq -- "-AtStartup" "$registration"
grep -Fq -- "-ExecutionTimeLimit ([TimeSpan]::Zero)" "$registration"
grep -Fq -- "-RestartCount" "$registration"
grep -Fq -- "-Daily" "$backup_registration"

grep -Fq "LISTEN_URL=http://127.0.0.1:5071" "$environment_example"
grep -Fq "AUTH_TOKEN_FILE=" "$environment_example"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -File "$repo_root/scripts/test-task-server-windows-assets.ps1" \
    -AssetRoot "$asset_root" \
    -TunnelRoot "$repo_root/deploy/windows/agent-runner-tunnel"
else
  printf 'pwsh is unavailable; skipped the PowerShell parse and bootstrap-import dry run.\n' >&2
fi

printf 'Task Server Windows assets carry a single-file win-x64 profile, session-independent S4U registration, detached supervised start, and a daily verified backup task.\n'
