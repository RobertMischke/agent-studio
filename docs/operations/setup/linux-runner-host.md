# Linux runner host (standalone remote runner)

Status: Remote daemon runbook. The runner continuously executes server-assigned
projects on a Linux host while retaining the RM-5 one-task diagnostic mode.

Related work: AGT-2092 (runner operations baseline) and AGT-2094 (Admin UI
remote-host onboarding). AGT-2094 uses this runbook as its operational
reference; the UI does not maintain a second copy of these commands.

This is the operator-facing companion to the plan in
[../../research/remote-ready-kickoff-2026-07.md](../../research/remote-ready-kickoff-2026-07.md)
(Phase 1 + Phase 3) and the binding lease contract in
[../../concepts/parallel-task-execution.md](../../concepts/parallel-task-execution.md)
§8.2C. The runner code lives under [`runner/`](../../../runner) and consumes only
the Runner API surface added by RM-3 (fenced lease) and RM-4 (log + artifact
upload).

## What the standalone runner is

A single self-contained .NET console process (`agent-runner`) that runs on a
Linux host and fills a bounded set of task slots without owning task state:

- **Code arrives via git `origin`** - the runner fetches and checks out a branch
  read-only. It never pushes; the platform still owns git integration on the
  server side (ADR-0019/0050/0057).
- **Results leave via the API** - CLI output goes to `POST /api/runner/logs`,
  evidence files under `results/` go to `POST /api/runner/artifacts`, and the
  final outcome is reconciled with `POST /api/tasks/{taskKey}/external-completion`
  so the card re-enters the local board.
- **Exactly-one-runner is enforced by the fenced lease** - acquire mints a
  fencing token, a heartbeat renews it, and a rejected heartbeat (`StaleToken` /
  `Expired`) means another holder took over, so the runner abandons the run
  instead of racing it (the §8.2C split-brain guard, enforced runner-side in
  `runner/LeaseHeartbeat.cs`).
- **Assignment is server-owned** - `ProjectSettings.executionRunner` names the
  daemon that may claim a project's cards. The remote claim endpoint and the
  local in-process runner read that same record, so config drift cannot cause
  double pickup. Lease fencing remains the hard takeover guard.
- **Every slot has its own linked git worktree** under
  `$RUNNER_WORKDIR/worktrees/<task-key>`. The shared `repo/` checkout is only the
  git metadata/fetch source. Completed task worktrees are removed after handoff.

### MVP boundaries (read before relying on it)

- **No code integration from the remote run.** The runner uploads evidence and a
  summary, not a git diff. A run that produces committable code changes still
  needs the platform's own commit/push path; that cutover is later remote-ready
  work, not this MVP.
- **Suitability is explicit and narrow.** `remoteExecutionEnabled` defaults to
  true at project level. Set it false only for machine-bound work such as the
  UpdateService Windows machinery or live-checkout drift scans. Headless
  Chromium, UI tests, and screenshots are remote-capable and use the host-owned
  Mode-A stack.
- **The CLI invocation is configurable, not hard-coded.** Headless auth and
  print-mode flags differ per CLI and per version; `RUNNER_CLI_BIN` /
  `RUNNER_CLI_ARGS` select them. See the per-CLI defaults below.

## Test host

`agent-runner` (Hetzner, `88.99.136.78`). SSH key-auth, one sudo-capable user.
No inbound ports beyond SSH until the central-URL auth work (Phase 2) lands, so
the runner reaches the Task Server over an `ssh -R`/`-L` tunnel or the operator's
LAN address during the MVP. For **unattended** operation, keep that tunnel up as
a supervised, auto-reconnecting service and gate work on its health-check:
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

## Product onboarding from Remote Hosts

The primary setup path is **Workspace Settings -> Remote hosts -> Set up
runner**. The action creates a normal visible CLI task, so the existing task
conversation owns live output, operator input, completion, and durable history.
The local controller then runs
[`scripts/remote-runner-onboard.sh`](../../../scripts/remote-runner-onboard.sh);
every provisioning command in that controller is executed through SSH on the
selected host.

Before the task can start, the dialog requires an SSH target, the registered
host client id, a credential-free git origin, and one of these Task Server
topologies:

| Topology | URL entered in setup | Required proof |
|---|---|---|
| Central | Authenticated TLS URL, for example `https://tasks.example.com` | Remote `curl --max-time 10 <url>/healthz` succeeds. Do not expose the workstation with only `X-Client-Id`; it is attribution, not authentication. |
| Tunnel | Remote listener, normally `http://127.0.0.1:15031` | The supervised reverse-tunnel unit from [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md) is active and the remote health probe succeeds. |
| LAN | Protected workstation LAN address and bound Task Server port | Firewall and access controls are explicit, and the remote health probe succeeds. |

`http://localhost:5031` and `http://127.0.0.1:5031` name the remote host itself
when evaluated there. Setup rejects them for central and LAN modes. Tunnel mode
must name the listener that actually exists on the remote host. A failed probe
stops before package installation and prints central URL, tunnel, and LAN
remediation instead of waiting on a runner request.

The controller is intentionally repeatable after a host wipe:

1. Verify SSH key access, passwordless sudo, .NET 10, and Task Server health
   from the host.
2. Install or update the `CodingAgentRunner` NuGet global tool and require
   version `0.5.0` or newer, then install the Codex and Claude CLIs.
3. Run host-owned login flows. Codex uses `codex login --device-auth`; the URL
   and one-time code stay visible in the task conversation. Claude uses
   `claude auth login --claudeai`. The operator completes browser steps locally,
   then `codex login status` and `claude auth status --text` report the active
   account. Credential files are never copied as the normal path.
4. Atomically write `/etc/agent-runner/runner.env` with the Task Server URL,
   runner identity, `RUNNER_CLIENT_ID`, and git origin. Install and start the
   service through systemd. The SSH session never owns the daemon process.
5. Prove `systemctl is-enabled`, `systemctl is-active`, runner health, and the
   registered client endpoint before setup completes.

The NuGet package must be published with package type `DotnetTool` and expose
the `agent-runner` command. A library-only `CodingAgentRunner` package cannot be
installed with `dotnet tool`; setup reports that packaging mismatch explicitly
and does not silently switch to a source build. The source-publish procedure
below remains a troubleshooting and development fallback, not the product
onboarding path.

## 1. Provision the host

Ubuntu LTS. Install the runtime the runner and the agent CLIs need:

```bash
sudo apt-get update && sudo apt-get install -y git curl build-essential
# dotnet 10 SDK (or runtime) and node 22 via the usual channels, then:
npm i -g @anthropic-ai/claude-code @openai/codex
npx playwright install --with-deps chromium
```

### Per-host CLI credentials (D5, permanent)

Authenticate every CLI **on the host itself** so the host owns its own
credentials. Do **not** copy the operator's `~/.claude/.credentials.json` /
`~/.codex/auth.json` over from the studio. A seeded credential shares a
refresh-token lineage with the operator's account, so when the operator side
re-logs-in or rotates its token, the host's copy is invalidated and the host
drops out logged-out mid-batch. This drift was live on 2026-07-09 (host-claude
logged out after an operator-side token rotation, needing a manual re-seed); a
host that logged in independently is immune to it. Per-host login is the
permanent replacement for the earlier shared-credential seeding.

- **Claude.** Log in directly on the host, once, over an `ssh -L` port-forward so
  the OAuth browser step can complete (`claude`, finish onboarding), **or** mint a
  long-lived headless token on the host with `claude setup-token`. Either way the
  host holds its **own** refresh token, independent of the operator's. Verify with
  `claude --version` and one throwaway `claude -p "say hi"` before wiring the runner.
- **Codex.** Same rule: run `codex login` on the host so it writes the host's own
  `~/.codex/auth.json`; do not copy the operator's. Verify with `codex --version`.
- **Rotation is now per host.** If a host's own token is ever revoked, re-run that
  host's login / `setup-token` **on that host**. No other host and no operator-side
  action is involved, so there is no cross-host drift to chase.

The host's `~/.claude/.credentials.json` must stay a plain file the runner user
can read and write in place, so Claude's own token refresh persists for the next
launch. (The studio's clean-context mechanism keeps the same in-place invariant
for parallel runs by sharing the one credential file *by link* rather than
copying it - AGT-2066 "OAuth token roulette"; see the clean-context section of
[`docs/cli/supported-clis.md`](../../cli/supported-clis.md).)

## 2. Build the runner

```bash
git clone <origin> agent-taskboard && cd agent-taskboard
dotnet publish runner/AgentRunner.csproj -c Release -o /opt/agent-runner
```

The output binary is `agent-runner`.

## 3. Configure

Every value has an environment-variable default (systemd-friendly); the per-task
identifiers can also be passed as flags. `agent-runner --help` prints the full
list.

| Env var | Flag | Default | Meaning |
|---|---|---|---|
| `RUNNER_SERVER_URL` | `--server` | `http://127.0.0.1:5030` | Task Server base URL (or the tunnelled address). |
| `RUNNER_ID` | `--runner-id` | `agent-runner-<host>` | Stable lease owner identity. Fencing is per task, not per pid. |
| `RUNNER_NAME` | `--runner-name` | `agent-runner-01` | Board-facing runner/project name. |
| `RUNNER_CLIENT_ID` | `--client-id` | (self-register) | Existing host identity shown in Remote Hosts. When set, startup verifies this exact X-Client-Id and refuses to create a replacement identity. |
| `RUNNER_GIT_REMOTE` | `--git-remote` | (required) | Origin the code is fetched from. |
| `RUNNER_BRANCH` | `--branch` | (base branch) | Branch to check out for the run. |
| `RUNNER_BASE_BRANCH` | `--base-branch` | `main` | Fallback when the task branch is absent on origin. |
| `RUNNER_WORKDIR` | `--workdir` | `$TMPDIR/agent-runner-work` | Where the repo checkout and `results/` live. |
| `RUNNER_CLI_BIN` | `--cli` | `claude` | Agent CLI binary (or a wrapper script). |
| `RUNNER_CLI_ARGS` | `--cli-args` | `-p` | Headless CLI args; the prompt is streamed on stdin. |
| `RUNNER_AUTH_TOKEN` | `--auth-token` | (none) | Bearer token for the central URL (Phase 2 auth). |
| `RUNNER_TTL_SECONDS` | `--ttl` | `120` | Requested lease TTL; the server clamps it. |
| `RUNNER_HEARTBEAT_SECONDS` | | `30` | Renew cadence, kept below the TTL. |
| `RUNNER_RUN_TIMEOUT_SECONDS` | | `3600` | Hard cap on a single CLI run. |
| `RUNNER_MAX_PARALLELISM` | `--max-parallelism` | `2` | Maximum concurrent task slots on this host. |
| `RUNNER_POLL_SECONDS` | `--poll-seconds` | `5` | Delay after an empty claim poll. |

Recommended per-CLI headless defaults (verify against your installed version):

- Claude: `RUNNER_CLI_BIN=claude`, `RUNNER_CLI_ARGS="-p"` (prompt on stdin, text
  output on stdout that the runner scans for the `[[TASK_*]]` sentinel).
- Codex: `RUNNER_CLI_BIN=codex`, plus the non-interactive exec flags your version
  exposes. When quoting gets awkward, point `RUNNER_CLI_BIN` at a small wrapper
  script instead of fighting the space-split arg parser.

## 4. Assign projects and run the daemon

Assignment is stored through the Task Server API. The following assigns a
remote-capable project to this daemon; use an empty `executionRunner` to hand it
back to the local runner:

```bash
curl -X PUT "$RUNNER_SERVER_URL/api/projects/my-project/execution-runner" \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: <registered-operator-client-id>' \
  -d '{"executionRunner":"agent-runner-01","remoteExecutionEnabled":true}'
```

Start the foreground daemon with no task argument or with `--poll`:

```bash
export RUNNER_SERVER_URL=http://<studio-host>:5030
export RUNNER_GIT_REMOTE=<origin>
export RUNNER_NAME=agent-runner-01
export RUNNER_MAX_PARALLELISM=2
/opt/agent-runner/agent-runner --poll
```

The daemon registers once, polls `POST /api/runner/claim`, and fills free host
slots. The server only returns pickup-eligible `2-ready` cards from assigned,
remote-capable projects and moves a successful fenced claim to `3-progress`.

### systemd deployment

Install the shipped unit and an environment file, then enable it:

```bash
sudo install -D -m 0644 deploy/systemd/agent-runner.service /etc/systemd/system/agent-runner.service
sudo install -d -m 0750 /etc/agent-runner /var/lib/agent-runner
sudoedit /etc/agent-runner/runner.env
sudo systemctl daemon-reload
sudo systemctl enable --now agent-runner
sudo journalctl -u agent-runner -f
```

At minimum, `runner.env` sets `RUNNER_SERVER_URL`, `RUNNER_GIT_REMOTE`,
`RUNNER_ID`, and `RUNNER_NAME`. Product onboarding also sets
`RUNNER_CLIENT_ID`, so the configured identity and its `LastSeen` record remain
stable across reinstalls. The unit restarts after failures, logs to journald,
requests graceful SIGINT shutdown, and best-effort starts
`~/bin/stack-start.sh` before the daemon so host-local screenshot runs have a
clean Mode-A Studio stack.

## 5. Run one task end-to-end for diagnostics

1. On the **local board**, assign the ready task to project **`agent-runner-01`**
   (the project the remote runner serves) and note its task key.
2. On the **runner host**:

   ```bash
   export RUNNER_SERVER_URL=http://<studio-host>:5030
   export RUNNER_GIT_REMOTE=<origin>
   export RUNNER_BRANCH=task/<the-task-branch>     # optional; falls back to base
   /opt/agent-runner/agent-runner <TASK-KEY>
   ```

The runner then, in order: **preflights connectivity** (probes `/healthz`, so a
dropped tunnel is reported cleanly *before* any lease or CLI work), **registers
its client identity** (see below), acquires the fenced lease, starts
heartbeating, checks out the branch from origin, fetches `prompt.md` over the
API, spawns the CLI in the working tree, ships stdout/stderr to the server every
few seconds, uploads everything under `results/`, posts the external-completion,
and releases the lease. Exit code `0` means a clean handoff; `1` a
blocked/needs-input outcome; `2` lease not granted; `3` lease lost mid-run; `4`
the task server was unreachable or rejected a call.

For unattended operation, run `agent-runner --health-check` as a readiness probe
(exit `0` reachable, `4` not) before assigning a task, and keep the tunnel up as
a service. Both are covered in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

### Client identity registration (required)

The Task Server guards every mutation behind an `X-Client-Id` registration
boundary (`ClientIdentityMiddleware`): a POST from an id the server has never
seen is rejected `401 client-unknown`. Reads (prompt fetch) stay open, but the
lease, log, artifact, and external-completion writes do not. Product onboarding
sets `RUNNER_CLIENT_ID` to the existing host identity. Startup authenticates a
`GET /api/clients/{id}` with that header before its first write; an unknown or
retired id is a hard error, and the runner does not create a replacement. This
verification also refreshes `LastSeen` through the normal middleware path.

When `RUNNER_CLIENT_ID` is omitted for a manual/legacy install, the runner
self-registers before its first write: it POSTs `RUNNER_NAME` to
`/api/clients/register` (an open-path route) and adopts the server-assigned id.
Registration is idempotent on the name. A `401 client-unknown` therefore points
to a wrong configured id or a reverse proxy stripping `X-Client-Id`.

## 6. Acceptance walkthrough

The task passes RM-5 acceptance when, after the runner exits `0`:

- the card has moved on the **local** board (default `5-human-review`) with an
  `external_completion` timeline entry sourced from `agent-runner-01`;
- `logs/cli-output.log` on the server shows the remote CLI output; and
- the uploaded evidence is present under the task's `results/` folder and in the
  workspace evidence commit.

For the full Remote Hosts acceptance, also record the setup task id, the exact
Task Server URL/topology, `systemctl is-enabled` and `is-active`, both CLI auth
status outputs, and the runner client id from `GET /api/clients`. Its
`lastSeenAt` must become fresh after the daemon begins polling. Finally assign a
Ready probe task through the normal project execution setting and verify that
the remote runner badge, fenced lease timeline, CLI log upload, result upload,
and external completion all name the same runner. This is the AGT-1923 probe
mechanic; do not substitute the static frontend readiness fixture for this
proof.

## Troubleshooting

- **No task is claimed** - confirm the project's `executionRunner` exactly
  matches `RUNNER_NAME` or `RUNNER_ID`, `remoteExecutionEnabled` is true, and
  the card is pickup-eligible in `2-ready`. The local runner intentionally skips
  the same assigned project.
- **`lease not granted: Held` in one-task mode** - another runner already holds
  the task. The daemon claim path normally avoids this before launch.
- **`connection lost: cannot reach the task server ...` at startup** - the
  preflight `/healthz` probe failed, almost always a dropped reverse tunnel.
  Confirm with `agent-runner --health-check`; if it also exits `4`, restart the
  tunnel service ([remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)).
  The runner refuses at preflight by design, so no half-started lease or CLI is
  left behind.
- **`lease lost: StaleToken` mid-run** - a TTL takeover happened; the network was
  slow enough that heartbeats missed the window. Raise `RUNNER_TTL_SECONDS` /
  lower `RUNNER_HEARTBEAT_SECONDS`, or check the tunnel.
- **`Task '<key>' has no prompt.md`** - the task key did not resolve on the
  server, or the job folder has no prompt. Confirm the key against the board.
- **No output shipped** - the server rejects logs for an unknown task key; the
  console still shows the CLI output locally. Check `RUNNER_SERVER_URL` and the
  task key.
- **CLI exits immediately / wrong flags** - the headless flags do not match the
  installed CLI version. Adjust `RUNNER_CLI_ARGS` or wrap the CLI in a script.
- **`401 client-unknown` on a lease/log/upload call** - the runner's
  `X-Client-Id` never reached the server, so it looks unregistered. The runner
  registers itself automatically, so this almost always means a reverse proxy or
  tunnel is dropping the `X-Client-Id` request header; forward it, or point
  `RUNNER_SERVER_URL` straight at the Studio.
