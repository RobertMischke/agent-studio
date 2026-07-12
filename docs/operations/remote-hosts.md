# Remote hosts runbook

This runbook is the operator path for adding, connecting, draining, retiring,
reviving, and permanently removing a remote runner host. The detailed Linux
installation reference remains [linux-runner-host.md](setup/linux-runner-host.md).

## Add a host

Open **Workspace Settings > Remote hosts > Add host**. The wizard introduced in
AGT-1922 asks for a stable runner name such as `agent-runner-01` and an SSH
target such as `runner@host.example.com`.

On Ubuntu, install the base tools and the runner dependencies:

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
git -C /opt/agent-runner/source remote set-url origin https://github.com/ORG/REPO.git
git -C /opt/agent-runner/source remote set-url --push origin git@github-agent-studio:ORG/REPO.git
git -C /opt/agent-runner/source push --dry-run origin HEAD
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
each project repository to move from one remote project to all projects remote;
the fallback URL above remains the startup probe repository.

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
```

Enable and verify the service:

```bash
sudo systemctl enable --now agent-runner
sudo systemctl is-active agent-runner
sudo journalctl -u agent-runner -n 100 --no-pager
curl -sS https://tasks.example.com/api/clients/agent-runner-01
```

The host card reports daemon state (`running`, `read-only`, or `stopped`), last
claim, active and free slots, running task count, task inflow, last contact, and
push status. If last contact is older than five minutes, live numbers are hidden
and the card says when the host was last seen.

## Drain

Use **Drain** before maintenance. Drain blocks new leases immediately. Running
tasks keep their leases and finish normally. The host remains visible and
drained afterward. The equivalent API call is:

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
sudo systemctl restart agent-runner
```

## Remove permanently

Permanent removal is only available for an already-retired client. It deletes
the identity record and cannot be undone. Use it only after deciding that the
revive path and the visible historical host entry are no longer needed.

```bash
curl -sS -X DELETE https://tasks.example.com/api/clients/agent-runner-01/permanent \
  -H 'X-Client-Id: local-default'
```
