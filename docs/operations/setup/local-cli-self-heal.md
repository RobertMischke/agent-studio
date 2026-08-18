# Local CLI Self-Heal

Version: 2026-08-18 (AGT-2673)
Status: Operator runbook for local CLI install health on the control-plane host.

The control plane runs coding-agent CLIs that are installed as global npm
packages. Twice within six days the `claude` command vanished from `PATH` on the
Windows control-plane host while the package itself stayed on disk. Until this
card, that only became visible through drained pickups: probes happened at boot
and immediately before a spawn, so a host that was healthy at boot could fail at
minute twelve with no surface other than a paused lane.

The backend now probes local CLI installs on a loop, tells the two broken shapes
apart, repairs the one shape it safely can, and reports every repair.

## The failure shapes

| Shape | What it looks like on disk | Who repairs it |
|---|---|---|
| `Ready` | The CLI answers `--version` | nobody |
| `ShimMissingPackagePresent` | Package under the npm global `node_modules`, no bin shim, no rename leftovers | this feature, `npm install --global <package>` |
| `PackageBroken` | Orphaned `.claude.cmd-<random>` shims, or a shim that exists but will not run (the ~500-byte postinstall stub) | [`tools/check-cli-shims.sh`](../../../tools/check-cli-shims.sh) at boot and `NpmShimHealer` pre-spawn |
| `NotInstalled` | No package and no shim | the operator, deliberately |
| `Unknown` | The CLI does not run and the host has no npm global bin to inspect | the operator |

The distinction that matters: `ShimMissingPackagePresent` means the operator's
intent to have this CLI is still recorded on disk, so restoring it is a repair.
`NotInstalled` means the CLI was never installed or was removed on purpose, and
installing it automatically would be the product making a decision that is not
its to make. The classification is a pure function with a direct matrix test in
`backend.Tests/LocalCliInstallDiagnosisTests.cs`.

## What the backend does

`backend/Features/HostHealth/` owns the flow, ordered as boundary validation,
coordination, pure decision, then bounded side effects:

1. `LocalCliHealthHostedService` ticks every five minutes (`HostHealth:CliProbeIntervalMinutes`).
2. `CliRouterVersionProbe` asks the existing CLI layer for the `--version` verdict.
3. `LocalCliInstallInspector` reads the npm global bin: shim presence, orphan
   shims, package directory, and the installed version from `package.json`.
4. `LocalCliInstallDiagnosis` classifies the facts into one of the shapes above.
5. `LocalCliRepairThrottle` allows at most one automatic repair per CLI per hour
   (`HostHealth:CliRepairWindowMinutes`). An operator-requested repair bypasses it.
6. `GlobalNpmPackageInstaller` runs `npm install --global <package>`, the only
   side effect this feature owns.
7. `LocalCliRepairJournal` appends one row to `<workspace>/logs/cli-repairs.jsonl`.

The feature does not touch runner mode. Once the CLI answers again, the existing
CLI-recovery resume in `ProjectRunner.TickCliRecoveryResume` sees availability
flip and restores the operator's desired mode by itself.

Only `claude` (`@anthropic-ai/claude-code`) and `codex` (`@openai/codex`) are
covered. Antigravity/Gemini is not a single global npm package on the control
plane, so there is no reinstall remedy to offer.

## What the operator sees

- **Status bar.** A quiet note, `claude CLI repaired at 10:04`, with the version
  change in the tooltip. It fades out after 24 hours because a repair that
  worked is history, not an acute state.
- **Status bar, acute.** `claude CLI repair failed` with a warning tone, shown
  until that CLI is healthy again. This is the only alarm the feature raises.
- **Backend log.** `Information` for a successful repair, `Error` for a failed one.
- **`<workspace>/logs/cli-repairs.jsonl`.** The durable record, next to
  `infra-halts.jsonl` and `pickup-failures.jsonl`.

## Root-cause capture

Each repair row carries the evidence needed to prove or disprove the auto-update
hypothesis:

- `versionBefore` / `versionAfter` and `packageVersionBefore` / `packageVersionAfter`.
  Both occurrences moved the installed version (2.1.231 to 2.1.234), which is
  what put auto-update under suspicion in the first place.
- `npmActivity`: npm debug logs from the 30 minutes before the breakage was
  observed, by file name and mtime. npm writes one log per invocation, so an
  entry here is an npm run that overlapped the breakage. Logs stamped *after*
  the observation are excluded on purpose, so the repair's own npm invocation
  cannot be mistaken for the trigger.
- `installerOutput`: the tail of what npm said, so a failed repair explains itself.

Log contents are never read; only names, timestamps, and sizes are recorded.

## API

| Route | Purpose |
|---|---|
| `GET /api/v1/host-health/cli` | Current diagnosis per CLI plus recent repair notes. Read-only. |
| `POST /api/v1/host-health/cli/{cliType}/repair` | Operator-requested repair. Bypasses the rate limit; 400 for a CLI this host cannot reinstall. |

## Configuration

| Key | Default | Purpose |
|---|---|---|
| `HostHealth:CliSelfHealEnabled` | `true` | Turns the periodic probe and automatic repair off. |
| `HostHealth:CliProbeIntervalMinutes` | `5` | Probe cadence. |
| `HostHealth:CliRepairWindowMinutes` | `60` | Minimum gap between automatic repairs of the same CLI. |
| `HostHealth:NpmGlobalBin` | platform default | Explicit npm global bin directory. Windows uses `%APPDATA%\npm`; POSIX derives `<prefix>/bin` and `<prefix>/lib/node_modules` from `NPM_CONFIG_PREFIX` or the first existing candidate prefix. |

## Break-and-heal rehearsal

Run this on the host itself when you want to confirm the wiring end to end. It
never touches the real npm install: everything is pointed at a scratch
directory, and the reinstall is a stub script that does what a real global
install would do.

```sh
ROOT=/tmp/cli-self-heal-rehearsal
mkdir -p "$ROOT"/{npm/node_modules/@anthropic-ai/claude-code,stubbin,workspace}

# A stand-in CLI and its installed package.
printf '#!/bin/sh\necho "2.1.231 (Claude Code)"\n' > "$ROOT/npm/claude"
chmod +x "$ROOT/npm/claude"
printf '{"name":"@anthropic-ai/claude-code","version":"2.1.231"}' \
  > "$ROOT/npm/node_modules/@anthropic-ai/claude-code/package.json"

# A stub npm that restores the shim the way a real reinstall would.
cat > "$ROOT/stubbin/npm" <<'STUB'
#!/bin/sh
printf '#!/bin/sh\necho "2.1.234 (Claude Code)"\n' > "$ROOT/npm/claude"
chmod +x "$ROOT/npm/claude"
printf '{"name":"@anthropic-ai/claude-code","version":"2.1.234"}' \
  > "$ROOT/npm/node_modules/@anthropic-ai/claude-code/package.json"
echo "added 1 package"
STUB
chmod +x "$ROOT/stubbin/npm"

PATH="$ROOT/stubbin:$PATH" \
TaskRepository="$ROOT/workspace" \
HostHealth__NpmGlobalBin="$ROOT/npm" \
HostHealth__CliSelfHealEnabled=false \
ClaudeCli__Path="$ROOT/npm/claude" \
  dotnet run --project backend/OrchestratorApi.csproj
```

Then, in a second shell:

```sh
H='X-Client-Id: local-default'
B=http://127.0.0.1:5030/api/v1/host-health

curl -s -H "$H" $B/cli                       # expect claude state "Ready"
rm -f /tmp/cli-self-heal-rehearsal/npm/claude  # break it the way the host broke
curl -s -H "$H" $B/cli                       # expect "ShimMissingPackagePresent" / "GlobalReinstall"
curl -s -X POST -H "$H" $B/cli/claude/repair # expect "Ready" and lastRepairSucceeded true
cat /tmp/cli-self-heal-rehearsal/workspace/logs/cli-repairs.jsonl
```

To rehearse the alarm, replace the stub npm with one that exits non-zero: the
repair row records `repaired: false` with npm's output, the backend logs at
`Error`, and the status bar shows `claude CLI repair failed`.

The automated equivalent of this rehearsal is
`backend.Tests/HostHealthEndpointsTests.cs` (`MachineBound`; it starts a real
child process).

## Related

- [`tools/check-cli-shims.sh`](../../../tools/check-cli-shims.sh) - boot-time repair for the
  orphan-shim and postinstall-stub shapes.
- [CLI domain map](../../system/domains/cli.md) - system of record for CLI changes.
- [Admin CLI onboarding](../../concepts/admin-cli-onboarding.html) - the wider
  "make CLI health visible" concept this is one step of.
- `CrossSlugInfraCircuitBreaker` - the safety net that halts pickup when a
  broken CLI is draining the Ready lane.
