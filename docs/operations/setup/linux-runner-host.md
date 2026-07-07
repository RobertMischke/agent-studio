# Linux runner host — provisioning & headless CLI auth runbook

**Status.** Phase-1 output of the remote-ready theme
([remote-ready-kickoff-2026-07.md](../../research/remote-ready-kickoff-2026-07.md),
ADR-0059). First verified end-to-end on 2026-07-07 against the Hetzner test
host. This runbook is the reproducible path from "bare Ubuntu" to "all runner
pieces proven": claude/codex CLIs authenticated headlessly, Playwright
rendering, backend building.

## 1. Current test host

| | |
|---|---|
| Provider | Hetzner Server-Börse (dedicated), auction #3031485 |
| Hardware | i7-8700 (6C/12T), 64 GB RAM, 2×512 GB NVMe as software RAID1 |
| OS | Ubuntu 24.04 LTS (installimage, ext4, `/boot` + swap 8G + `/`) |
| Access | SSH key-auth only (`PasswordAuthentication no`, `PermitRootLogin prohibit-password`) |
| Users | `root` (key), `agent` (key, `sudo NOPASSWD`) — all runner work happens as `agent` |

Operator convenience: the operator machine carries `~/.ssh/config` aliases
`agent-runner` (→ `agent@<host>`) and `agent-runner-root`, both using the
dedicated `~/.ssh/agent-studio-runner` ed25519 key (no passphrase — accepted
for the test host; per-machine keys + rotation are the D4/D5 follow-up).
Recovery path if keys are lost: Hetzner Rescue System.

## 2. Base provisioning (as root, once)

```bash
apt-get update && apt-get install -y git curl build-essential python3
# node 22 (NodeSource)
curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y nodejs
# dotnet 10 SDK (dotnet-install.sh into /usr/lib/dotnet, symlink into PATH)
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/lib/dotnet
ln -sf /usr/lib/dotnet/dotnet /usr/local/bin/dotnet
# coding-agent CLIs
npm install -g @anthropic-ai/claude-code @openai/codex
# runner user
adduser --disabled-password --gecos "" agent
usermod -aG sudo agent && echo "agent ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/agent
install -d -m 700 -o agent -g agent /home/agent/.ssh
cp /root/.ssh/authorized_keys /home/agent/.ssh/ && chown agent:agent /home/agent/.ssh/authorized_keys
# playwright browsers + OS deps (as agent)
su - agent -c "npx --yes playwright install --with-deps chromium"
# ssh hardening: PasswordAuthentication no, PermitRootLogin prohibit-password → systemctl reload sshd
```

`DOTNET_ROOT=/usr/lib/dotnet` must be exported for `dotnet` SDK commands in
non-login shells (e.g. over plain `ssh host 'command'`).

## 3. Headless CLI auth (decision D5 — credential seeding)

No browser exists on the host; interactive OAuth is replaced by seeding the
credential files from an already-authenticated machine. **Exact file set:**

| CLI | File on host | Mode | Content |
|---|---|---|---|
| claude | `~/.claude/.credentials.json` | `600` | OAuth access+refresh token (copy from operator machine) |
| claude | `~/.claude.json` | `644` | minimal onboarding state, see below |
| codex | `~/.codex/auth.json` | `600` | copy from operator machine |

Minimal `~/.claude.json` (skips trust/theme/upsell wizard that would
otherwise dead-lock a headless run):

```json
{
  "hasCompletedOnboarding": true,
  "theme": "dark",
  "autoUpdates": false
}
```

Seeding from the operator machine (PowerShell/Git-Bash):

```bash
ssh agent-runner 'mkdir -p ~/.claude ~/.codex && chmod 700 ~/.claude ~/.codex'
scp ~/.claude/.credentials.json agent-runner:~/.claude/.credentials.json
scp ~/.codex/auth.json          agent-runner:~/.codex/auth.json
ssh agent-runner 'chmod 600 ~/.claude/.credentials.json ~/.codex/auth.json'
# then write ~/.claude.json as above
```

**Verification (must both pass):**

```bash
ssh agent-runner 'claude --version && claude -p "Reply with exactly: RUNNER-OK"'
ssh agent-runner 'codex exec --skip-git-repo-check "Reply with exactly: CODEX-OK"'
```

### Known risk: refresh-token drift

The seeded refresh token is *shared* with the operator machine. If both sides
rotate it independently, one side can get logged out. Observed remedy: re-seed
`.credentials.json` from whichever machine still has a valid session. This is
carried as kickoff risk "headless subscription auth drift"; the durable
answer (per-host `claude setup-token` long-lived tokens or per-host accounts)
is part of D5's rotation decision — revisit before Phase 3 (multiple
runners). `autoUpdates: false` keeps the CLI version pinned so probe/PTY
behavior only changes when we choose to update.

## 4. Smoke battery (Phase-1 baseline, 2026-07-07)

| Check | Command | Result 2026-07-07 |
|---|---|---|
| claude headless | `claude -p "Reply with exactly: RUNNER-OK"` | ✅ `RUNNER-OK` (2.1.202) |
| codex headless | `codex exec --skip-git-repo-check …` | ✅ `CODEX-OK` (0.142.5) |
| Playwright | `npx playwright screenshot --browser chromium https://example.com /tmp/pw.png` | ✅ 15.9 KB PNG |
| repo clone | `git clone https://github.com/RobertMischke/agent-studio.git` | ✅ public, no deploy key needed (read) |
| backend build | `dotnet build agent-taskboard.sln` | ✅ 0 errors (63 pre-existing warnings) |
| backend tests | `dotnet test --no-build` | ⚠️ 3295/3337 green; 23 known Linux failures — see kickoff doc "Phase 1 findings" |
| backend boot | `dotnet run --project backend` + Linux `appsettings.Local.json` | ✅ `/api/projects` serves registry with Unix paths |
| quota probe (Porta.Pty) | `POST /api/cli/quota/refresh/claude` (`X-Client-Id` header!) | ✅ full PTY scrape on Ubuntu 24.04, plan + windows parsed |
| E2E task run | `POST /api/tasks` + `POST /api/tasks/{id}/start` | ✅ claude spawned, worktree flow (ADR-0057), artifact written, lane → 4-auto-review. Note: repo needs a `develop` branch; `watchPath` = `<RootPath>/.orchestrator/jobs` for in-repo layout |

## 5. Operational notes

- **Process containment:** plain `nohup … &` over ssh does *not* reliably
  survive session teardown; use `setsid` (or a systemd unit, which is the
  D6 target anyway). This is the same orphan-containment gap the kickoff
  lists for CLI grandchildren — Linux needs process-group reaping
  (`setsid` + `kill -pgid`) before Phase 3.
- The backend reads `backend/appsettings.Local.json` (gitignored) for
  `TaskRepository` + `WatchPaths`; on the host, point `TaskRepository` at a
  local path and seed a demo store via
  `node scripts/seed-demo-workspace.mjs --root <path>` (ADR-0056).
- Nothing on the host listens publicly; only SSH is exposed. Keep it that
  way until Phase-2 auth lands (kickoff §5, D4).
