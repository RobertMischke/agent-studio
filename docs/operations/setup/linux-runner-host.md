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

## 5. Operator hands-on — exploring the host

Everything below is safe to run as `agent` (alias `ssh agent-runner`).

**Where things live:**

| Path | What |
|---|---|
| `~/agent-taskboard` | clone of agent-studio (backend + frontend), branch `main` |
| `~/agent-taskboard/backend/appsettings.Local.json` | host-local config: `TaskRepository` + `WatchPaths` |
| `~/taskboard-workspace` | the task store (git-backed), `projects/<key>/tasks/<lane>/` |
| `~/projects/website` | pilot project checkout (cloned from the local mirror) |
| `~/git/website.git` | bare mirror the operator pushes into (see §6) |
| `~/smoke-project` | throwaway repo from the Phase-1 E2E smoke |
| `/tmp/ass-worktrees/<project>/<task>` | per-task git worktrees (ADR-0057) |
| `~/coding-agent-chat` | chat-lib clone + build (the frontend's `file:` dependency) |
| `~/bin/stack-start.sh`, `~/bin/stack-stop.sh` | start/stop the full stack |
| `/tmp/backend.log`, `/tmp/ngserve.log` | stack logs |

**Look around / verify the pieces:**

```bash
ssh agent-runner                      # log in
htop                                  # what is running / load (q to quit)
claude -p "Reply with exactly: OK"    # CLI auth still alive?
git -C ~/agent-taskboard log --oneline -3   # which code version is on the host
```

**Start / stop the full stack (backend :5030 + board UI :4010):**

```bash
~/bin/stack-start.sh    # logs: /tmp/backend.log, /tmp/ngserve.log
~/bin/stack-stop.sh
```

(The scripts wrap `dotnet run --project backend` and
`ng serve frontend --port 4010 --proxy-config proxy.conf.json` with `setsid`.
Two hard-won details inside: the frontend's `@coding-agent/chat` is a
`file:`-dependency, so `~/coding-agent-chat` must be cloned + built and its
transitive dep `lowlight` installed `--no-save`; and never `pkill -f` a
pattern that appears in your own ssh command line — it kills your session.)

**See the product from your own browser** (nothing is exposed publicly —
the tunnel is the only door): the operator machine's `~/.ssh/config` carries
a `studio-remote` alias with `LocalForward 14010 127.0.0.1:4010` and
`LocalForward 15030 127.0.0.1:5030` (local ports deliberately ≠ 4010/5030,
which the local dev stack may occupy). Then:

```bash
ssh -N studio-remote     # foreground, Ctrl+C disconnects
```

and open `http://localhost:14010` — the full board UI, served and executed
on the Linux host. `-N` means "no shell, forwarding only";
`ExitOnForwardFailure yes` makes a port collision fail loudly instead of
silently tunneling nothing.

**Watch a run live:**

```bash
tail -f /tmp/backend-smoke.log                          # runner + API log
watch -n2 'pgrep -af "claude|codex" | grep -v pgrep'    # spawned CLIs
ls /tmp/ass-worktrees/*/*/                              # what the task changed
curl -s localhost:5030/api/cli/quota | python3 -m json.tool | head -30
```

## 6. How code reaches the host — git sync & security assessment

**Two channels, both deliberate:**

1. **Public repos** (agent-studio itself): plain `https` clone/fetch from
   GitHub. Read-only by construction — the host holds **no GitHub
   credentials at all**, so it *cannot* push to GitHub, delete branches, or
   read private repos even if fully compromised.
2. **Private repos** (pilot: the website): the **operator pushes over SSH
   into a bare mirror** on the host (`git push runner main` →
   `~/git/website.git`); the working clone pulls from that mirror. Again: no
   GitHub secret ever touches the host. Results flow back the same way —
   task branches (`task/<id>`) land in the mirror, and the operator runs
   `git fetch runner` locally to review them. The sync is **manual and
   operator-initiated in both directions**, which is the right default while
   the host is a test environment.

**What secrets *do* live on the host, and the blast radius:**

| Secret | Risk if host is compromised | Mitigation |
|---|---|---|
| `~/.claude/.credentials.json` | attacker can consume the Claude subscription and act as the CLI identity | revocable from the operator side (re-login rotates); `autoUpdates` off; monitor /usage |
| `~/.codex/auth.json` | same for the OpenAI account | revocable |
| `~/.ssh/authorized_keys` (public keys) | none (public halves) | — |
| project code in `~/projects` | disclosure — currently code that is public anyway or website content | don't mirror sensitive repos to the test host |

**Deliberately absent:** GitHub tokens/deploy keys, task-server credentials
(none exist yet — Phase 2), TLS private keys, any inbound service beyond
`sshd`. The exposure of the box is: SSH (key-only, root prohibited) — and
outbound HTTPS to github.com/Anthropic/OpenAI.

**When this changes (Phase 3, per `parallel-task-execution.md` §8.2C):**
multiple runners will need unattended fetch/push. The contract there is
**per-runner deploy keys with least privilege** (read + push only to
`task/*` refs of assigned repos), never a personal token. The current
zero-credential state is the baseline we degrade from *knowingly*, one
scoped key at a time.

## 7. Operational notes

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
