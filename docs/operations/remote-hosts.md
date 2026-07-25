# Remote hosts runbook

This runbook is the operator path for adding, connecting, draining, retiring,
reviving, and permanently removing a remote agent host. The detailed Linux
installation reference remains [linux-runner-host.md](setup/linux-runner-host.md).

## Add a host

Open **Workspace Settings > Remote hosts > Add host**. The wizard introduced in
AGT-1922 asks for a stable runner name such as `agent-runner-01` and an SSH
target such as `runner@host.example.com`.

On Ubuntu, install the base tools and the agent host dependencies:

```bash
sudo apt-get update
sudo apt-get install -y git curl build-essential
npm i -g @anthropic-ai/claude-code @openai/codex
npx playwright install --with-deps chromium
```

Authenticate each CLI on the host. Do not copy credential files from the
operator workstation:

```bash
claude auth login --claudeai
claude auth status --text
codex login --device-auth
codex login status
```

## Give the host push identity

The organization must allow repository deploy keys. Generate a unique key for
each host and repository:

```bash
install -d -m 700 ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/agent-studio-deploy -C 'agent-runner-01:agent-taskboard' -N ''
cat ~/.ssh/agent-studio-deploy.pub
```

Register the printed public key on the repository with write access, using the
repository API or its **Settings > Deploy keys** UI. Then configure SSH to use
that key and keep fetch and push identities separate:

```sshconfig
Host github-agent-studio
  HostName github.com
  User git
  IdentityFile ~/.ssh/agent-studio-deploy
  IdentitiesOnly yes
```

```bash
git -C /opt/agent-host/source remote set-url origin https://github.com/ORG/REPO.git
git -C /opt/agent-host/source remote set-url --push origin git@github-agent-studio:ORG/REPO.git
git -C /opt/agent-host/source push --dry-run origin HEAD
```

Set the same URLs for the daemon:

```bash
RUNNER_GIT_REMOTE=https://github.com/ORG/REPO.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:ORG/REPO.git
```

The host card must show **Writable: yes** and the latest push-probe date before
it receives work. **Writable: no** means the daemon is read-only and the server
refuses new claims.

AGT-2141 added per-project repository URLs and isolated shared clones. Configure
each project repository to move from one remote project to all projects remote.
The fallback URLs above remain startup probe inputs only. Each project clone
uses its registry URL for both fetch and push and repairs both values on every
refresh.

The first claim for each host/project pair is a preflight offer, not a lease.
The daemon prepares the project's real shared clone, requires its fetch and push
URLs to match the registered repository URL, fetches, then creates and removes
a temporary runner ref. This real write exercises server-side hooks and
permissions that a dry-run can miss. The green result is cached until that
project registration changes. A failure keeps the card Ready and appears with
its reason on both the host card and the project's Execution card.

## Connect the daemon

Register the host identity once and use its returned `id` as
`RUNNER_CLIENT_ID`. Every daemon request presents that value as `X-Client-Id`:

```bash
curl -sS -X POST https://tasks.example.com/api/clients/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"agent-runner-01","kind":"service"}'
```

Write `/etc/agent-runner/runner.env` with at least:

```bash
RUNNER_SERVER_URL=https://tasks.example.com
RUNNER_ID=agent-runner-01
RUNNER_NAME=agent-runner-01
RUNNER_CLIENT_ID=agent-runner-01
RUNNER_GIT_REMOTE=https://github.com/ORG/REPO.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:ORG/REPO.git
RUNNER_MAX_PARALLELISM=2
# Optional repository-specific requirements:
RUNNER_REQUIRED_CAPABILITIES=toolchain:dotnet,toolchain:node,toolchain:playwright
```

Enable and verify the service:

```bash
sudo systemctl enable --now agent-host
sudo systemctl is-active agent-host
sudo journalctl -u agent-host -n 100 --no-pager
curl -sS https://tasks.example.com/api/clients/agent-runner-01
```

The host card reports daemon state (`running`, `read-only`, or `stopped`), last
claim, active and free slots, running task count, task inflow, last contact, and
push status. If last contact is older than five minutes, live numbers are hidden
and the card says when the host was last seen.

## Capability admission

Each coding and review service refreshes a versioned capability advertisement
every minute. The Task Server treats an advertisement as fresh for three
minutes. New claims declare their role, provider authentication, source,
repository, disk, Task Server connectivity, and any configured toolchain
requirements. Review claims also add the immutable subject's semantic, vision,
Git, or source-bundle requirements.

A first correlated fault marks one capability suspect. Repetition drains that
capability and starts a bounded cooldown. When cooldown expires, exactly one
matching claim becomes the half-open canary. An authoritative typed coding
terminal, with an immutable handoff when required, or an authoritative
non-infrastructure review report reopens normal capacity. Product findings
prove that review infrastructure recovered without becoming a product pass.
Canary failure returns to a longer cooldown. Do not lower
`RUNNER_MAX_PARALLELISM` as a repair. Healthy capabilities and unrelated
services on the same host continue using the configured slots.

Remote Hosts shows the capability state, reason, first and last failure,
cooldown, canary claim, affected coding and review attempts, and recovery
history. A stale advertisement is explicitly stale. AGT-2142 telemetry appears
as live meters only while both its sample and the host heartbeat are fresh.

Automatic whole-host drain is reserved for shared foundations: disk full,
invalid lease authority, host network isolation, repository filesystem
corruption, or Task Server authority uncertainty. The UI labels it
**Automatic whole-host drain**. An operator action is stored and labeled
separately as **Operator-requested host drain**. Both block new claims, but
neither kills an existing lease.

## Drain

Use **Drain** before maintenance. Drain blocks new leases immediately. Running
tasks keep their leases and finish normally. The host remains visible and
drained afterward. The equivalent API call is:

Drain is deliberately not a live migration command. Runner assignment changes,
immediate stop-and-switch, cross-host continuation, and the historical A → B → A
route follow the
[runner provenance and host handoff contract](../concepts/completion-review-and-remote-runner-stability.html#provenance).

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/drain \
  -H 'X-Client-Id: local-default'
```

## Retire

Use **Retire**, read the confirmation, then choose **Drain and retire**. If work
is running, the server drains first and changes the identity to `retired` only
after the daemon reports zero active slots. With zero active slots it retires
immediately. Retired clients move into the
collapsed **Retired clients** section and retain historical attribution.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/retire \
  -H 'X-Client-Id: local-default'
```

## Revive

Expand **Retired clients** and choose **Revive**. Start or re-register the daemon
afterward so `LastSeenAt`, daemon state, and the push probe become fresh again.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/revive \
  -H 'X-Client-Id: local-default'
sudo systemctl restart agent-host
```

## Remove permanently

Permanent removal is only available for an already-retired client. It deletes
the identity record and cannot be undone. Use it only after deciding that the
revive path and the visible historical host entry are no longer needed.

```bash
curl -sS -X DELETE https://tasks.example.com/api/clients/agent-runner-01/permanent \
  -H 'X-Client-Id: local-default'
```
