# Linux runner host (standalone remote runner)

Status: Remote daemon runbook. The runner continuously executes server-assigned
projects on a Linux host while retaining the RM-5 one-task diagnostic mode.

Related work: AGT-2092 (runner operations baseline) and AGT-2094 (Admin UI
remote-host onboarding). AGT-2094 uses this runbook as its operational
reference; the UI does not maintain a second copy of these commands.

This is the operator-facing companion to the plan in
[../../concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
and the binding lease contract in
[../../concepts/parallel-task-execution.md](../../concepts/parallel-task-execution.md)
§8.2C. The runner code lives under [`runner/`](../../../runner) and consumes only
the Runner API surface added by RM-3 (fenced lease) and RM-4 (log + artifact
upload).

## What the standalone runner is

A single self-contained .NET console process (`agent-runner`) that runs on a
Linux host and fills a bounded set of task slots without owning task state:

- **Code arrives and leaves via git `origin`** - the runner fetches over the
  credential-free URL and pushes with a write-enabled deploy key dedicated to
  this host and repository. The daemon proves that push identity at startup;
  a failed probe leaves the host read-only and blocks new claims.
- **Results leave through a durable outbox** - protocol 2 journals CLI output,
  status, artifacts, Git facts, terminal facts, and the final result envelope
  under `$RUNNER_WORKDIR/outbox/<run-attempt-id>/` before sending them. The
  Task Server acknowledges one immutable result, then accepts an idempotent
  completion. `Done` and `NoOp` enter normal review; blocked, input, and
  genuinely unknown outcomes remain typed. Remote runs are not labelled as
  out-of-band completions.
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
  `$RUNNER_WORKDIR/<project-id>/worktrees/<task-key>`. Each project has a shared
  clone at `$RUNNER_WORKDIR/<project-id>/repo`; it is fetched before a claimed
  task starts. A coding worktree is removed only after the Task Server has
  durably acknowledged the matching immutable result envelope.

### Remote Review Executor service

Run review as a second systemd identity, even when it shares the physical host
with coding. Set `RUNNER_ROLE=review`, use a different `RUNNER_ID`, service
account, credential file, cgroup quota, and `RUNNER_REVIEW_WORKDIR`. Do not point
the review root at `RUNNER_WORKDIR`.

The Review Executor advertises Git/source-bundle, semantic, and vision
capabilities. Each claimed ReviewAttempt receives a fresh workspace, cache,
temporary directory, eight-port block, Compose namespace, database namespace,
and fenced cleanup lifecycle. Child processes start from a cleared environment.
Only names in `RUNNER_REVIEW_CREDENTIAL_ENV` are admitted; the corresponding
service credentials must be read-only. Coding deploy keys and write-enabled
provider credentials must not be present in the review unit.

The executor fetches the immutable result ref or verified Git bundle, proves
repository identity, HEAD, tree, and clean state, then proves HEAD again before
every completion, build/test, requirement, quality, documentation, evidence,
artifact, or vision command. A missing ref reports
`ReviewInfra/SnapshotUnavailable`; there is no coding-worktree or Task Server
checkout fallback.

### MVP boundaries (read before relying on it)

- **Push capability is explicit.** Each host/repository assignment has its own
  deploy key and push URL. The platform still owns integration policy, while
  the remote runner may publish its task branch through that bounded identity.
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
For the local profile, expose no inbound Task Server port and reach it through a
supervised `ssh -R`/`-L` tunnel. For the networked profile, the runner connects
outbound to the authenticated HTTPS origin with its enrolled service identity.
The tunnel procedure and health gate are documented in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

## Product onboarding from Remote Hosts

The primary setup path is **Workspace Settings -> Remote hosts -> Set up
runner**. The action creates a normal visible CLI task, so the existing task
conversation owns live output, operator input, completion, and durable history.
The local controller then runs
[`scripts/remote-runner-onboard.sh`](../../../scripts/remote-runner-onboard.sh);
every provisioning command in that controller is executed through SSH on the
selected host.

Before the task can start, the dialog requires an SSH target, a credential-free
fallback git origin, and one of these Task Server topologies. The local profile
also needs a registered attribution id. The networked profile instead needs the
owner-enrolled `runner_<id>` and a protected `rnr.*` credential file already on
the host:

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
   runner identity, optional `RUNNER_CLIENT_ID`, credential-file path, and fallback git origin. Install and start the
   service through systemd. The SSH session never owns the daemon process.
5. Prove `systemctl is-enabled`, `systemctl is-active`, Runner health, and an
   authenticated claim or empty-queue response before setup completes.

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
[`docs/system/cli/supported-clis.md`](../../system/cli/supported-clis.md).)

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
| `RUNNER_CLIENT_ID` | `--client-id` | (none) | Optional attribution label. It is not authentication and grants no access. |
| `RUNNER_GIT_REMOTE` | `--git-remote` | (none) | Credential-free fetch URL and startup push-probe repository. Required for daemon onboarding; normally use HTTPS. |
| `RUNNER_GIT_PUSH_REMOTE` | `--git-push-remote` | (fetch URL) | Write URL installed as Git `origin.pushurl`; normally the SSH URL backed by this host/repository deploy key. |
| `RUNNER_BRANCH` | `--branch` | (base branch) | Branch to check out for the run. |
| `RUNNER_BASE_BRANCH` | `--base-branch` | `main` | Fallback when the task branch is absent on origin. |
| `RUNNER_WORKDIR` | `--workdir` | `$TMPDIR/agent-runner-work` | Where the repo checkout and `results/` live. |
| `RUNNER_ROLE` | `--role` | `coding` | `coding` or the separately registered `review` service. |
| `RUNNER_REVIEW_WORKDIR` | `--review-workdir` | `$TMPDIR/agent-review-work` | Disposable review-only workspace, cache, temp, and evidence root. Must differ from `RUNNER_WORKDIR`. |
| `RUNNER_REVIEW_CREDENTIAL_ENV` | `--review-credential-env` | (none) | Comma-separated read-only credential variable names admitted into the cleared review environment. |
| `RUNNER_STATE_DIR` | `--state-dir` | `$RUNNER_WORKDIR/.runner-state` | Durable slot, attempt, PID, worker result, and file-backed output state used for planned restart reattachment. Keep it on persistent local storage. |
| `RUNNER_CLI_BIN` | `--cli` | `claude` | Agent CLI binary (or a wrapper script). |
| `RUNNER_CLI_ARGS` | `--cli-args` | `-p` | Headless CLI args; the prompt is streamed on stdin. |
| `RUNNER_CLI_RESUME_ARGS` | `--cli-resume-args` | (none) | Optional provider-specific same-session arguments containing the literal `{sessionId}` placeholder. A supported infrastructure failure resumes at most once; an invalid session falls back to durable salvage once and then escalates. |
| `RUNNER_AUTH_TOKEN_FILE` | `--auth-token-file` | (none on loopback) | Protected file containing the owner-enrolled Runner service credential. Required for every non-loopback Task Server. |
| `RUNNER_AUTH_TOKEN` | none | (none) | Compatibility environment input. Prefer the credential file so the secret is absent from process diagnostics. |
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

### Multi-repository clone layout and eligibility

The claim response contains the durable project id, repository URL, and default
branch. The daemon uses the project id as the cache key, so two projects never
share git metadata even when their task keys happen to look alike:

```text
$RUNNER_WORKDIR/
  PROJ-001/repo
  PROJ-001/worktrees/AGT-2141
  PROJ-007/repo
  PROJ-007/worktrees/QS-104
```

The Task Server resolves the repository URL from the project's registry URL
entry whose id or label is `repo` (label `repository` is also accepted). If that
entry is absent, it derives `remote.origin.url` and the default branch from the
registered local `RepositoryPath`. Local filesystem remotes are not usable on a
different host. An assigned project with no resolvable network repository URL
is skipped before a lease is created and is logged as
`remote-runner-project-skipped`; the card remains Ready and is not escalated.

`RUNNER_GIT_REMOTE` is also the repository used by the daemon's one-time startup
write probe. Configure it together with `RUNNER_GIT_PUSH_REMOTE` for the
host/repository assignment. Do not point one global push URL at unrelated claim
repositories.

### Push identity setup

The recommended identity is one write-enabled repository deploy key per runner
host and repository. Generate it as the systemd runner user. Only the public key
leaves the host:

```bash
install -d -m 0700 ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/agent-studio-deploy -C 'agent-runner-01:agent-studio' -N ''
cat ~/.ssh/agent-studio-deploy.pub
```

In GitHub, an organization owner must first allow repository deploy keys in the
organization security/settings policy. If the GitHub API returns `422 Deploy
keys are disabled for this repository`, this organization policy is still off.
After it is enabled, add the public key under repository Settings, Deploy keys,
select **Allow write access**, and keep the private key on the runner. Pin its
use without changing the fetch URL:

```sshconfig
Host github-agent-studio
  HostName github.com
  User git
  IdentityFile ~/.ssh/agent-studio-deploy
  IdentitiesOnly yes
```

```bash
RUNNER_GIT_REMOTE=https://github.com/agent-orc/agent-studio.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:agent-orc/agent-studio.git
```

As a fallback when organization policy cannot allow deploy keys, create a
fine-grained personal access token owned by a dedicated machine account. Limit
it to this repository with **Contents: Read and write**, store it in the runner
user's OS credential helper, and keep the HTTPS URL free of embedded secrets.
Do not put a token in `runner.env`, a command line, task output, or evidence.

At daemon startup, the runner performs `git push --dry-run` to
`refs/heads/runner-capability-probe/<runner-id>`, publishes `ready` or
`read-only` on its client identity, and then polls. Dry-run creates no branch.
The server refuses claims from a `read-only` runner, and Remote Hosts shows a
Read-only badge with the probe error. Restore credentials and restart the unit;
the next startup probe replaces the status.

### Remote completion protocol

The Task Server returns the operator-authored `prompt.md` verbatim. Immediately
before spawning either CLI, the standalone runner appends the same mandatory
completion protocol used by the local runner. It requires exactly one final
`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`,
`[[TASK_NEEDS_INPUT:<reason>]]`, or `[[TASK_NOOP]]` line. This assembly happens
inside the shared task execution path, so daemon claims and one-task diagnostics
cannot drift. The shipped log contains
`remote-completion-protocol appended to task prompt` before the spawn line.

Codex `exec --json -` output remains JSONL on stdout. The scanner consumes the
complete stdout buffer after the process and asynchronous readers have drained;
the sentinel inside an `item.completed` agent message is recognized without a
Codex-version-specific event parser.

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
export RUNNER_NAME=agent-runner-01
export RUNNER_MAX_PARALLELISM=2
/opt/agent-runner/agent-runner --poll
```

The daemon registers once, polls `POST /api/runner/claim`, and fills free host
slots. The server only returns pickup-eligible `2-ready` cards from assigned,
remote-capable projects and moves a successful fenced claim to `3-progress`.

Ready Epic containers are eligible for a special remote planning claim. They
consume one slot and use the normal lease, heartbeat, telemetry, drain, and
cancellation lifecycle. The server supplies the same rendered Epic
decomposition prompt used by the local runner. The daemon creates a bounded,
detached checkout for read-only repository inspection and removes it after the
run without creating or pushing a runner branch. A valid plan creates child
coding cards and sends the Epic to auto-review. Empty or invalid output, or any
attempted source mutation, returns the Epic to Backlog.

The startup Git push probe still describes coding capability. A host whose
identity reports `read-only` may claim Epic planning, but it receives no normal
coding claims until push capability is restored.

At startup the daemon reads `RUNNER_STATE_DIR` before making a new claim. A
persisted attempt is adopted only when its worker PID still has the recorded
start time and `/proc/<pid>/cwd` resolves to the recorded worktree. The daemon
then restores the same lease, fence, Task Server run id, and attempt instance,
and follows the worker's JSONL output file from the persisted sequence. A
worker that finished during the short restart window is finalized from its
atomically written result file. A missing process, reused PID, or worktree
mismatch is never heartbeated: the lease is actively released and the Task
Server returns the Progress card to Ready with the next claim using a higher
fence. This recovery preserves the bounded attempt/autonomy contract; it does
not create a second attempt or an autonomous task store on the Runner.

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

At minimum, `runner.env` sets `RUNNER_SERVER_URL`, `RUNNER_ID`, `RUNNER_NAME`,
`RUNNER_GIT_REMOTE`, and `RUNNER_GIT_PUSH_REMOTE`. A networked deployment also
sets `RUNNER_AUTH_TOKEN_FILE`; the credential itself stays in that separate
protected file. `RUNNER_CLIENT_ID` remains optional attribution. The unit restarts after failures, logs to journald,
requests graceful SIGTERM drain, and best-effort starts
`~/bin/stack-start.sh` before the daemon so host-local screenshot runs have a
clean Mode-A Studio stack.

The shipped unit deliberately uses `KillMode=process`. This is required:
`control-group` kills detached job workers and makes safe reattachment
impossible. `StartLimitIntervalSec=300`, `StartLimitBurst=5`, and
`RestartSec=10s` bound a broken-binary restart loop while allowing ordinary
recovery. Installing or changing the unit requires root, followed by
`systemctl daemon-reload`.

### Planned daemon restart and deploy

A planned Runner deploy no longer waits for host idle. Replace the published
files and restart the main service process:

```bash
sudo systemctl restart agent-runner
sudo journalctl -u agent-runner --since '-2 minutes' \
  | grep -E 'planned shutdown|persisted attempt accepted|recovered .* persisted slot|releasing dead persisted attempt'
```

On SIGTERM the old daemon stops making claims, leaves detached job workers
running, flushes its already-atomic slot records, and exits. systemd starts the
replacement, which verifies and reattaches those workers before opening any
freed slot to claims. Confirm every previously occupied slot reports either
`persisted attempt accepted` or `releasing dead persisted attempt`. The latter
must be followed by a Ready card and a later higher-fence claim. Do not change
the unit back to `KillMode=control-group`.

This procedure covers a planned daemon binary restart, not a machine reboot,
power loss, Task Server authority restart, or forced `SIGKILL`. Those cases
still use the existing fenced containment and `process-unknown` recovery
contracts.

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
API, spawns the CLI in the working tree, journals and ships stdout/stderr,
journals everything under `results/`, secures the exact result on an immutable
remote ref, obtains the durable Task Server acknowledgement, removes the
worktree, posts the idempotent fenced completion, and releases the lease.
Exit code `0` means a clean handoff; `1` a
blocked/needs-input outcome; `2` lease not granted; `3` lease lost mid-run; `4`
the task server was unreachable or rejected a call.

For unattended operation, run `agent-runner --health-check` as a readiness probe
(exit `0` reachable, `4` not) before assigning a task, and keep the tunnel up as
a service. Both are covered in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

### Durable result handoff before teardown

The runner never removes a checkout that contains work available only on the
host. This rule applies to success, missing terminal sentinels, failure,
cancellation, timeout, graceful systemd shutdown, and debris recovered when the
next process starts:

1. Inspect `git status` in the task worktree.
2. Commit dirty and untracked files with
   `wip(runner): salvage before teardown - outcome <X>`.
3. Preserve the moving salvage ref at `runner/<runner-id>/<task-key>` using only
   a normal create or fast-forward push.
4. Publish the exact result to
   `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>` and verify that ref.
5. Send the immutable result envelope. It binds repository ID, source
   RunAttempt ID, base SHA, result SHA, immutable ref, artifact-manifest digest,
   and applicable submodule or LFS identities.
6. Persist the matching Task Server acknowledgement locally.
7. Remove the linked worktree only after that acknowledgement. A local commit
   or the moving salvage branch alone is not sufficient.

If upload, push, the connection, the runner process, or the Task Server fails,
the daemon replays the original monotonic outbox before claiming new work.
Idempotency keys make a lost response safe. A transfer failure is retried
without starting the coding CLI and without consuming coding or completion
budget. `transfer-recovery` means the host still owns recoverable transfer work.

Operators can inspect durable server projections without reading host files:

```bash
curl -sS https://tasks.example.com/api/v1/management/status
curl -sS https://tasks.example.com/api/v1/management/outboxes
curl -sS https://tasks.example.com/api/v1/runs/<run-id>/result-handoff \
  -H 'X-Task-Protocol-Version: 2'
```

The status projection includes total backlog, oldest unacknowledged sequence,
and counts by final handoff state. Each outbox row includes its RunAttempt.
Result envelopes are retained through task completion and for at least
`TaskServer:ResultRetentionDays` afterward, default 30 days. Automatic deletion
is not currently enabled.

On a later pickup, the retained local tip and the existing canonical salvage
ref are compared by ancestry. Local-ahead is published by normal fast-forward;
remote-ahead remains authoritative. If the tips diverge, the runner leaves the
canonical ref unchanged and publishes the retained local tip to the deterministic
collision ref
`runner/<runner-id>/<task-key>-collision-<local-sha>-<remote-sha>`. It verifies
both exact tips, then prepares the new checkout from the canonical SHA and starts
the CLI. Repeating the pickup reuses the same collision ref and creates no extra
history. No force push is used.

The result facts retain the salvage branch and commit. A divergent recovery also
records the collision ref, canonical and local SHAs, and the typed recovery
action. Each transfer pass makes three publish attempts. Exhaustion leaves the
worktree and both tips untouched, records `transfer-recovery`, and retries a
transfer pass with backoff. It does not complete, move, requeue, or launch a new
coding attempt. Do not delete that path manually. Restore origin access and let
the outbox converge.

### Local-profile client attribution

This section applies only to the loopback or protected-tunnel `local` profile.
For an internet-facing `networked` Task Server, open registration is disabled;
use the owner-authorized enrollment and `RUNNER_AUTH_TOKEN_FILE` flow in
[networked-task-server.md](./networked-task-server.md). In both profiles,
`X-Client-Id` is attribution and never authentication.

The local-profile Task Server guards every mutation behind an `X-Client-Id` registration
boundary (`ClientIdentityMiddleware`): a POST from an id the server has never
seen is rejected `401 client-unknown`. Reads (prompt fetch) stay open, but the
lease, log, artifact, and completion writes do not. Product onboarding
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

- a successful sentinel has moved the card on the **local** board to
  `4-auto-review` with an `agent_run_finished` timeline entry sourced from
  `agent-runner-01`, and no `external_completion` provenance;
- `logs/cli-output.log` on the server shows the remote CLI output; and
- the uploaded evidence is present under the task's `results/` folder and in the
  workspace evidence commit.

For the full Remote Hosts acceptance, also record the setup task id, the exact
Task Server URL/topology, `systemctl is-enabled` and `is-active`, both CLI auth
status outputs, and the runner client id from `GET /api/clients`. Its
`lastSeenAt` must become fresh after the daemon begins polling. Finally assign a
Ready probe task through the normal project execution setting and verify that
the remote runner badge, fenced lease timeline, CLI log upload, result upload,
and runner completion all name the same runner. This is the AGT-1923 probe
mechanic; do not substitute the static frontend readiness fixture for this
proof.

## Troubleshooting

- **No task is claimed** - confirm the project's `executionRunner` exactly
  matches `RUNNER_NAME` or `RUNNER_ID`, `remoteExecutionEnabled` is true, and
  the card is pickup-eligible in `2-ready`. Also inspect the backend log for
  `remote-runner-project-skipped`: the project needs a network URL in its `repo`
  registry entry or an origin derivable from `RepositoryPath`. The local runner
  intentionally skips the same assigned project.
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
- **`outcome Unknown` after a substantive final reply** - first confirm the run
  log contains `remote-completion-protocol appended to task prompt`. Its absence
  means the host is running a pre-AGT-2148 runner build. If the line is present,
  inspect the final stdout event and verify that the agent emitted one canonical
  `[[TASK_*]]` token; Codex JSONL is supported directly and stdout is not tail
  truncated by the runner.
- **`401 client-unknown` on a lease/log/upload call** - the runner's
  `X-Client-Id` never reached the server, so it looks unregistered. The runner
  registers itself automatically, so this almost always means a reverse proxy or
  tunnel is dropping the `X-Client-Id` request header; forward it, or point
  `RUNNER_SERVER_URL` straight at the Studio.
## Reading host telemetry

The runner samples the host every 30 seconds and piggybacks the sample on its existing Task Server claim poll. The Remote Hosts view keeps CPU, memory, Linux load averages, swap traffic, CPU steal time, I/O wait, core count, and active runner slots together. Use the 1h, 6h, 48h, and 14d controls to compare load with concurrency. For example, `6 active slots · load 6.4 of 12 cores` is direct evidence for whether the current slot limit leaves headroom.

Linux values come from `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, and `/proc/vmstat`. Windows runners report CPU and memory where the operating system exposes them without an additional agent; Linux-only fields remain empty. Raw 30-second samples are retained for 48 hours. Older samples are compacted into five-minute averages and retained for 14 days. The series is persisted below the workspace store in `telemetry/<client-id>.json`, so a backend restart does not erase it.

The host card raises these sustained findings after at least three consecutive samples:

- **VM throttled**: CPU steal time stays above 5 percent. On a virtual machine, this means the hypervisor is withholding scheduled CPU time.
- **Oversubscribed**: the one-minute load average stays above the reported core count. Compare the active-slots line before increasing parallelism.
- **Memory pressure**: combined swap-in and swap-out traffic stays above 64 KiB/s. A single historical swap allocation without traffic does not trigger this finding.

Short spikes remain visible in the quiet history chart but do not create a badge. Check I/O wait alongside CPU when load is high: high load with low CPU and elevated I/O wait usually points to storage contention rather than missing cores.
