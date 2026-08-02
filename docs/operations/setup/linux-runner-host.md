# Linux agent host (`agent-host`)

Status: Remote daemon runbook. `agent-host` continuously executes server-assigned
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

## What the agent host is

A single self-contained .NET console process (`agent-host`) that runs on a
Linux host and fills a bounded set of task slots without owning task state:

- **Code arrives and leaves via git `origin`** - the runner fetches over the
  credential-free URL and pushes with a write-enabled deploy key dedicated to
  this host and repository. A repository-specific delivery preflight proves
  that identity and target branch before the server grants a project lease.
  The daemon's startup probe covers only its configured fallback remote and is
  diagnostic.
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
account, credential file, and `RUNNER_REVIEW_WORKDIR`. The managed agent-host
unit supplies the role cgroup policy described in
[runner-host resource governance](../haertung-verteilte-ausfuehrung/target-architecture/resource-governance.md).
Do not point the review root at `RUNNER_WORKDIR`.

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

## Product onboarding from Execution Hosts

The primary setup path is **Workspace Settings -> Execution Hosts -> Set up agent
host**. The action creates a normal visible CLI task, so the existing task
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
   stable runner identity, optional `RUNNER_CLIENT_ID`, credential-file path,
   and fallback git origin. Install and start `agent-host.service` through
   systemd. The SSH session never owns the daemon process.
5. Prove `systemctl is-enabled`, `systemctl is-active`, agent-host health, and an
   authenticated claim or empty-queue response before setup completes.

The NuGet package must be published with package type `DotnetTool` and expose
the `agent-host` command. A library-only `CodingAgentRunner` package cannot be
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

## 2. Build agent-host

```bash
git clone <origin> agent-taskboard && cd agent-taskboard
sudo dotnet publish runner/AgentRunner.csproj -c Release -o /opt/agent-host
if [ -d /opt/agent-runner ] && [ ! -L /opt/agent-runner ]; then
  sudo mv /opt/agent-runner /opt/agent-runner.pre-agent-host
fi
sudo ln -sfnT /opt/agent-host /opt/agent-runner
```

The output binary is `/opt/agent-host/agent-host`. `/opt/agent-runner` is a
transition symlink for existing automation; new deployments and units use only
`/opt/agent-host`.

## 3. Configure

Every value has an environment-variable default (systemd-friendly); the per-task
identifiers can also be passed as flags. `agent-host --help` prints the full
list.

`RUNNER_*` remains the bootstrap-compatible canonical prefix. Every variable
also accepts the matching `AGENT_HOST_*` alias, for example
`AGENT_HOST_SERVER_URL`; when both forms are set, `RUNNER_*` wins. Stable
identity values such as `RUNNER_ID=agent-runner-01` are not renamed.

| Env var | Flag | Default | Meaning |
|---|---|---|---|
| `RUNNER_SERVER_URL` | `--server` | `http://127.0.0.1:5030` | Task Server base URL (or the tunnelled address). |
| `RUNNER_ID` | `--runner-id` | `agent-runner-<host>` | Stable lease owner identity. Fencing is per task, not per pid. |
| `RUNNER_NAME` | `--runner-name` | `agent-runner-01` | Board-facing runner/project name. |
| `RUNNER_CLIENT_ID` | `--client-id` | (none) | Optional attribution label. It is not authentication and grants no access. |
| `RUNNER_GIT_REMOTE` | `--git-remote` | (none) | Startup push-probe repository and legacy one-shot fallback. It is never inherited by a project clone. |
| `RUNNER_GIT_PUSH_REMOTE` | `--git-push-remote` | (fetch URL) | Startup push-probe and legacy one-shot write URL. It is never inherited by a project clone. |
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
| `RUNNER_TTL_SECONDS` | `--ttl` | `900` | Requested lease TTL; the server clamps it. The default grants a bounded 15-minute authority window so an already-claimed run can survive ten minutes of transport loss. |
| `RUNNER_HEARTBEAT_SECONDS` | | `30` | Renew cadence, kept below the TTL. |
| `RUNNER_RUN_TIMEOUT_SECONDS` | | `3600` | Hard cap on a single CLI run. |
| `RUNNER_MAX_PARALLELISM` | `--max-parallelism` | `2` | Bootstrap value for first host registration and fallback for an older server. The live ceiling is managed centrally in Execution Hosts. |
| `RUNNER_POLL_SECONDS` | `--poll-seconds` | `5` | Delay after an empty claim poll. |

Recommended per-CLI headless defaults (verify against your installed version):

- Claude: `RUNNER_CLI_BIN=claude`, `RUNNER_CLI_ARGS="-p"` (prompt on stdin, final
  response on stdout; the runner accepts a `[[TASK_*]]` sentinel only as its
  terminal standalone line).
- Codex: `RUNNER_CLI_BIN=codex`, plus the non-interactive exec flags your version
  exposes. The runner reads the last completed `agent_message`, not raw JSONL
  tool, diff, or diagnostic payloads. When quoting gets awkward, point
  `RUNNER_CLI_BIN` at a small wrapper script instead of fighting the space-split
  arg parser.

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

The Task Server resolves the repository URL only from the project's registry URL
entry whose id or label is `repo` (label `repository` is also accepted). The
registered local `RepositoryPath` may supply default-branch metadata, but its
origin is never used as a remote fallback. An assigned project without that
registry URL is reported as not remote-capable and skipped before a lease is
created. The server logs `remote-runner-project-not-remote-capable`; the card
remains Ready and no project clone is created.

On every project-clone contact, including the first clone, the daemon replaces
the complete `remote.origin.url` and `remote.origin.pushurl` value sets with the
project registry URL. This repairs stale existing clones and prevents the
startup probe or one-shot fallback from redirecting project pushes. The runner
emits `git-remote-configured` with `source=project-registry`, the project id,
and both effective URLs.

`RUNNER_GIT_REMOTE` and `RUNNER_GIT_PUSH_REMOTE` are used by the daemon's
one-time startup write probe. They verify baseline host capability only. Every
claimed project must have write credentials for its registry URL; the global
probe URLs do not rewrite project clone remotes.

### Push identity setup

Project clones keep their registered HTTPS URL for both fetch and push. The
simplest write identity is a fine-grained personal access token owned by a
dedicated machine account. Follow [Token requirements](#token-requirements)
before storing it in the runner user's credential helper. Keep the HTTPS URL
free of embedded secrets. Do not put a token in `runner.env`, a command line,
task output, or evidence.

### Token requirements

The coding runner must be able to publish ordinary source changes and changes
under `.github/workflows`. Use one of these exact permission sets:

| Token type | Repository selection | Required permissions |
|---|---|---|
| Fine-grained personal access token, preferred | Resource owner: the organization that owns the repository, or the personal account for a personally owned repository. Select only repositories assigned to this runner. | Repository permissions: **Contents: Read and write** and **Workflows: Read and write**. Metadata read access is added by GitHub. |
| Personal access token (classic), compatibility fallback | The token follows all repository access of its user. Use only when organization policy or a fine-grained-token limitation requires it. | Scopes: **`repo`** and **`workflow`**. For a public-only repository, `public_repo` plus `workflow` is sufficient, but the runner baseline uses `repo` because private repositories are supported. |

PATs are always tied to the user that creates them. A personal token is not an
organization identity. For an organization repository, prefer a dedicated
machine account, choose the organization as the fine-grained token's resource
owner, and wait for organization approval when its policy requires approval.
The account itself must have repository write access. Authorize a classic token
for the organization's SAML SSO when applicable. For a long-lived integration
that acts on behalf of an organization rather than one user, use a GitHub App
instead of sharing a human user's PAT.

Git credential lookup is path-sensitive when `useHttpPath` is enabled. The
repository URL with `.git` and the same URL without `.git` are different exact
keys. Store the same token under both keys. Run this as the systemd runner user
after configuring an appropriate credential helper:

```bash
git config --global credential.https://github.com.useHttpPath true

read -rp 'GitHub machine-account username: ' runner_github_user
read -rp 'Repository slug (OWNER/REPOSITORY): ' runner_repo_slug
read -rsp 'GitHub token: ' runner_github_token
printf '\n'

for runner_repo_path in "$runner_repo_slug" "$runner_repo_slug.git"; do
  printf 'protocol=https\nhost=github.com\npath=%s\nusername=%s\npassword=%s\n\n' \
    "$runner_repo_path" "$runner_github_user" "$runner_github_token" |
    git credential approve
done
unset runner_github_token

for runner_repo_url in \
  "https://github.com/$runner_repo_slug" \
  "https://github.com/$runner_repo_slug.git"; do
  GIT_TERMINAL_PROMPT=0 git ls-remote "$runner_repo_url" HEAD >/dev/null &&
    printf 'credential ok: %s\n' "$runner_repo_url"
done
unset runner_github_user runner_repo_slug runner_repo_path runner_repo_url
```

Never paste the token into a remote URL. `git credential approve` passes it on
standard input so it does not enter shell history. On a headless host, make sure
the selected credential helper persists for the runner user and protects its
storage with user-only permissions.

Set an expiration date and record the owner, repositories, permissions, expiry,
and runner hosts in the operator inventory. Rotate before expiry:

1. Create and approve the replacement token with the same repository selection
   and permissions.
2. Remove both old exact-match entries, then repeat the copy-paste storage block
   above with the replacement token:

   ```bash
   read -rp 'Repository slug (OWNER/REPOSITORY): ' runner_repo_slug
   for runner_repo_path in "$runner_repo_slug" "$runner_repo_slug.git"; do
     printf 'protocol=https\nhost=github.com\npath=%s\n\n' "$runner_repo_path" |
       git credential reject
   done
   unset runner_repo_slug runner_repo_path
   ```

3. Re-run both `git ls-remote` checks, restart `agent-host.service`, and confirm
   `Fallback repo: ok` plus `Fallback workflow: ok` in
   **Workspace Settings -> Execution Hosts**.
4. Revoke the old token only after every assigned repository and runner is
   green. A token owner's departure or repository-access removal also
   invalidates the runner identity and requires immediate rotation.

The guided installer tracked by AGT-2334 must link to this section and include a
**Create token** step before credential storage. That step shows the
fine-grained and classic checklists above, records both URL forms, runs both
read checks, then waits for the daemon capability result. Installer UI and
secret-handling implementation remain on the installer card.

A repository deploy key also works when the host uses an exact per-repository
Git URL rewrite for transport. Generate it as the systemd runner user. Only the
public key leaves the host:

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
use without changing the stored project remote:

```sshconfig
Host github-agent-studio
  HostName github.com
  User git
  IdentityFile ~/.ssh/agent-studio-deploy
  IdentitiesOnly yes
```

```bash
git config --global url."git@github-agent-studio:agent-orc/agent-studio.git".insteadOf \
  https://github.com/agent-orc/agent-studio.git
RUNNER_GIT_REMOTE=https://github.com/agent-orc/agent-studio.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:agent-orc/agent-studio.git
```

The exact rewrite keeps `remote.origin.url` and `remote.origin.pushurl` equal to
the registry value while Git uses the deploy-key SSH transport. Add one exact
rewrite per assigned repository. The `RUNNER_GIT_PUSH_REMOTE` value above is
still only the startup probe input.

At daemon startup, the runner first performs `git push --dry-run` to
`refs/heads/runner-capability-probe/<runner-id>`. It then commits a disabled
throwaway workflow with `[skip ci]`, pushes it to a unique branch below that
namespace, and immediately deletes the branch. This second push proves the
GitHub workflow permission that a dry-run cannot prove.

The runner publishes one of three statuses:

- `ready`: contents and workflow pushes succeeded.
- `ready-no-workflow-scope`: contents pushes succeeded, but GitHub rejected the
  workflow change. Claims remain enabled because card file scope is not known
  before execution.
- `read-only`: the configured fallback push path failed. Project claims still
  depend on their own repository delivery preflight.

Execution Hosts shows separate **Fallback repo** and **Fallback workflow**
badges without presenting either as fleet-wide delivery truth. A missing
workflow permission links back to this section. The same error classifier also
recognizes GitHub's first real workflow rejection; if salvage fails, its
`worktree-blocked` message includes the exact permission checklist and this
documentation path. Restore or rotate credentials and restart the unit; the
next startup probe replaces the fallback status.

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
/opt/agent-host/agent-host --poll
```

The daemon registers once, polls `POST /api/runner/claim`, and fills free host
slots. The server only returns pickup-eligible `2-ready` cards from assigned,
remote-capable projects and moves a successful fenced claim to `3-progress`.
Before the first lease for each host/project pair, the server offers the
registered repository without moving the card. The daemon creates or refreshes
`$RUNNER_WORKDIR/<project-id>/repo`, sets both `origin` URLs to the registered
URL, verifies them with `git remote get-url`, fetches, and runs
a real write probe that creates and removes a temporary
`runner/<runner-id>/delivery-preflight-*` ref. It reports that result in a
second claim request. A failed probe refuses the claim with its Git error and
leaves the card in `2-ready`. A successful result is cached for following
cards. Changing the project's repository registration or integration branch
invalidates it automatically.

Ready Epic containers are eligible for a special remote planning claim. They
consume one slot and use the normal lease, heartbeat, telemetry, drain, and
cancellation lifecycle. The server supplies the same rendered Epic
decomposition prompt used by the local runner. The daemon creates a bounded,
detached checkout for read-only repository inspection and removes it after the
run without creating or pushing a runner branch. A valid plan creates child
coding cards and sends the Epic to auto-review. Empty or invalid output, or any
attempted source mutation, returns the Epic to Backlog.

Epic planning uses the same repository-specific delivery preflight as coding
claims. A failure blocks only that project's claim and does not reduce the
host's slots for unrelated projects.

At startup the daemon reads `RUNNER_STATE_DIR` before registration or a new
claim. Each live worker directory contains a durable `lease-authority.json`
with the last server-issued expiry, a stop-before deadline one heartbeat before
expiry, and confirmed, uncertain, or rejected replay state. A persisted
deadline watcher runs while registration is unavailable. If stop-before is
reached, it reaps and verifies every process whose cwd belongs to the worktree,
records `authority-deadline-exhausted`, and retains the slot for honest server
reconciliation. It never starts a replacement while death is unproven.

A
persisted attempt is adopted only when its worker PID still has the recorded
start time and `/proc/<pid>/cwd` resolves to the recorded worktree. The daemon
then restores the same lease, fence, Task Server run id, and attempt instance,
and follows the worker's JSONL output file from the persisted sequence. A
worker that finished during the short restart window is finalized from its
atomically written result file. The slot enters `launching` before
`Process.Start`, and the worker writes its own atomic `worker.json` identity
before starting the CLI. Startup briefly waits for that identity, closing the
child-start-to-slot-save handoff window without trusting an unverified PID. A
missing process, reused PID, or worktree mismatch is never heartbeated: the
lease is actively released and the Task Server returns the Progress card to
Ready with the next claim using a higher fence. This recovery preserves the
bounded attempt/autonomy contract; it does not create a second attempt or an
autonomous task store on the Runner.

During a Task Server or transport outage, transient claim, heartbeat, event,
artifact, result-handoff, and completion failures do not terminate the daemon.
Already-claimed workers may continue only until their durable stop-before
deadline. Output and terminal facts accumulate in the monotonic local outbox;
new claims stop, and no queued report is replayed while authority is uncertain.
After connectivity returns, a successful exact-lease and exact-fence renewal
must reconcile authority before replay. Idempotency keys make retrying a lost
response safe. Task Server restart is different from transport recovery: it
quarantines the attempt as `process-unknown` and requires positive containment
proof before a higher-fenced replacement can start.

The per-project probe additionally gates the first claim, including an Epic
planning claim, because a project must have a proven delivery path before the
host takes any of its work. After a daemon interruption, the next claim requeues
assigned Progress work once its lease is free and issues a higher fencing token.

### systemd deployment

Use the product-owned host controller for the first install and every update.
The Coding example is:

```bash
bash scripts/remote-runner-onboard.sh \
  --host <ssh-target> \
  --server <task-server-url> \
  --topology central \
  --runner-id <enrolled-runner-id> \
  --runner-name agent-runner-01 \
  --role coding \
  --git-remote <fetch-url> \
  --git-push-remote <push-url>
```

Use `--role review`, a separate enrolled identity, and a separate credential
file to install or update `agent-runner-review.service`. The controller derives
role resource policy, writes it into the main unit, migrates legacy resource
drop-ins, runs `daemon-reload`, and restarts the selected service. Do not copy
`deploy/systemd/agent-runner.service` directly for a managed host; that file is
the legacy static unit reference and cannot derive a host quota.

At minimum, `runner.env` sets `RUNNER_SERVER_URL`, `RUNNER_ID`, `RUNNER_NAME`,
`RUNNER_GIT_REMOTE`, and `RUNNER_GIT_PUSH_REMOTE`. A networked deployment also
sets `RUNNER_AUTH_TOKEN_FILE`; the credential itself stays in that separate
protected file. `RUNNER_CLIENT_ID` remains optional attribution. The controller
installs `agent-host.service` and declares the transitional `agent-runner.service`
alias; new operations target `agent-host.service`. The unit restarts after
failures, logs to journald,
requests graceful SIGTERM drain, and best-effort starts
`~/bin/stack-start.sh` before the daemon so host-local screenshot runs have a
clean Mode-A Studio stack.

The managed units deliberately use `KillMode=process`. This is required:
`control-group` kills detached job workers and makes safe reattachment
impossible. `StartLimitIntervalSec=300`, `StartLimitBurst=5`, and
`RestartSec=10s` bound a broken-binary restart loop while allowing ordinary
recovery. Installing or changing the unit requires root, followed by
`systemctl daemon-reload`.

### Planned daemon restart and deploy

A planned Runner deploy no longer waits for host idle. Replace the published
files and restart the main service process:

```bash
sudo systemctl restart agent-host
sudo journalctl -u agent-host --since '-2 minutes' \
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
   /opt/agent-host/agent-host <TASK-KEY>
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

For unattended operation, run `agent-host --health-check` as a readiness probe
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
the daemon replays the original monotonic outbox only after authority has been
reconciled and before claiming new work. Idempotency keys make a lost response
safe. A transfer failure is retried without starting the coding CLI and without
consuming coding or completion budget. `transfer-recovery` means the host still
owns recoverable transfer work.

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
`TaskServer:ResultRetentionDays` afterward, default 30 days. The Task Server
runs the result-ref GC sweep immediately after startup and every
`TaskServer:ResultRefGcSweepMinutes` afterward, default six hours. Each pass is
bounded by `TaskServer:ResultRefGcBatchSize`, default 50. The sweep deletes an
immutable ref only when all of these facts hold:

- the retention deadline passed;
- the card is accepted in `6-completed` or `7-archive`;
- the matching review produced a terminal `Pass` or `ProductFailure` report
  and has no queued, leased, or process-unknown retry;
- a newer result-bearing RunAttempt exists for the card, so the source attempt
  is no longer the current review subject; and
- the ref exactly matches
  `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>`.

The newest result-bearing RunAttempt is retained even for an accepted or
archived card. This keeps current review and integration recovery
materializable. A missing
repository URL, a malformed ref, a Git error, or an unavailable credential
spares the ref. The Task Server service account therefore needs permission to
delete only the `refs/heads/agent-studio/results/**` namespace on each
registered origin. Repository URLs are never written to the sweep log. Every
pass emits one structured `result-ref-gc` line per deleted, spared, or failed
ref plus a summary, and successful deletion is persisted in the GC ledger.
Set `TaskServer:ResultRefGcEnabled=false` to pause remote deletion while
retention and safety facts continue to accumulate.

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

For the full Execution Hosts acceptance, also record the setup task id, the exact
Task Server URL/topology, `systemctl is-enabled` and `is-active`, both CLI auth
status outputs, and the runner client id from `GET /api/clients`. Its
`lastSeenAt` must become fresh after the daemon begins polling. Finally assign a
Ready probe task through the normal project execution setting and verify that
the remote host badge, fenced lease timeline, CLI log upload, result upload,
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
- **`Project delivery preflight failed`** - read the full reason on both the
  Execution Hosts card and the project's Execution card. Run the printed failing
  Git operation on the host against the registered repository URL and confirm
  the named integration branch exists. Repair that repository's credential,
  branch, or registration, then choose **Re-Probe** on the host card or wait for
  the five-minute proof expiry. A fallback-repository warning is diagnostic and
  does not override a successful project delivery preflight.
- **`connection lost: cannot reach the task server ...` at startup** - the
  preflight `/healthz` probe failed, almost always a dropped reverse tunnel.
  Confirm with `agent-host --health-check`; if it also exits `4`, restart the
  tunnel service ([remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)).
  The runner refuses at preflight by design, so no half-started lease or CLI is
  left behind.
- **Execution Hosts shows `Task Server route unreachable`** - the
  `task-server:connectivity` advertisement is stale or explicitly unavailable.
  This is the board-visible transport alarm even when the host itself is still
  running, because a broken route cannot carry a fresh failure report through
  itself. For the Windows-to-Linux reverse-tunnel topology, inspect
  `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\events.log`, then run the
  functional host-side curl from
  [Remote runner: persistent connection](./remote-runner-persistent-connection.md).
  Do not clear the symptom by restarting the review daemon; that discards
  non-reattachable in-flight review work.
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

The runner samples the host every 30 seconds and piggybacks the sample on its existing Task Server claim poll. The Execution Hosts view keeps CPU, memory, Linux load averages, swap traffic, CPU steal time, I/O wait, core count, active runner slots, and the last locally observed Task Server connection state together. Use the 1h, 6h, 48h, and 14d controls to compare load with concurrency. For example, `6 active slots · load 6.4 of 12 cores` is direct evidence for whether the current slot limit leaves headroom.

Task Server reachability has two complementary signals. The telemetry snapshot
contains the daemon's local observation, failure start, consecutive failure
count, escalation time, last error, and last recovery. The
`task-server:connectivity` capability carries a three-minute freshness deadline.
Freshness is the load-bearing remote alarm: when the route is down, the Task
Server cannot receive another telemetry sample, so the last sample must not be
misread as proof that the route remains healthy. The host card marks the route
unreachable as soon as the connectivity capability expires.

Linux values come from `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, and `/proc/vmstat`. Windows runners report CPU and memory where the operating system exposes them without an additional agent; Linux-only fields remain empty. Raw 30-second samples are retained for 48 hours. Older samples are compacted into five-minute averages and retained for 14 days. The series is persisted below the workspace store in `telemetry/<client-id>.json`, so a backend restart does not erase it.

The host card raises these sustained findings after at least three qualifying
samples. Up to two non-qualifying sample intervals are bridged so load that
flaps around a boundary remains one phase. The card shows at most one active
finding per kind. Completed phases are summarized per kind as an occurrence
count for the selected time window instead of appearing as individual badges.

- **VM throttled**: CPU steal time stays above 5 percent. On a virtual machine, this means the hypervisor is withholding scheduled CPU time.
- **Oversubscribed**: the one-minute load average stays above 1.5 times the
  reported core count and either CPU steal exceeds 5 percent or I/O wait exceeds
  10 percent. Review work is cgroup-capped and can remain runnable without
  displacing Coding, so high load alone is deliberately not treated as damage.
- **Memory pressure**: combined swap-in and swap-out traffic stays above 64 KiB/s. A single historical swap allocation without traffic does not trigger this finding.

Short spikes remain visible in the quiet history chart but do not create a badge. Check I/O wait alongside CPU when load is high: high load with low CPU and elevated I/O wait usually points to storage contention rather than missing cores.

Claim admission uses the same one-minute load sample. New claims stop only
after load divided by logical CPU cores remains above
`RUNNER_CLAIM_MAX_LOAD_PER_CORE` (default `1.5`) for
`RUNNER_LOAD_GATE_SUSTAINED_SECONDS` (default `120`). Existing runs continue,
and one recovery event is reported per sustained high-load interval.
