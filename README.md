# agent-orchestrator

**A management layer for coding-agent work.** Claude Code, Codex, GitHub
Copilot, Gemini, and other provider CLIs write the code. Agent Orchestrator
turns their runs into a visible, reviewable system of tasks, gates, evidence,
and project memory.

## Security first

The product is built for user-controlled environments and outbound-only remote
execution. Coding agents run through the CLI and credentials you configure.
Task Server owns durable card truth, leases, fences, policy, and global gates.
Runner hosts own local capacity, admission, checkouts, processes, and the
post-processing they execute. A host cannot mint authority, bypass a release
gate, or accept new work while the central authority is unavailable.

Network exposure is not assumed safe by default. Read the
[security overview](docs/operations/security/overview.md) and
[operator setup guide](docs/operations/setup/README.md) before exposing a
service beyond a trusted machine.

## The bottleneck is you

Coding agents can produce work faster than one person can remember, inspect,
and route it. The useful abstraction is not another chat window. It is a queue
that keeps intent, execution, evidence, review, and follow-up connected:

```text
idea -> prepared task -> admitted run -> evidence -> automated review
                                                    |
                         accepted <- human review <-+
                              |
                         next bounded task
```

The board shows where work is, the task detail explains what happened, and the
pipeline keeps deterministic ownership around the probabilistic agent run.

## Principles

| We keep | We deliberately skip |
|---|---|
| Provider-owned coding-agent CLIs | A hidden replacement agent runtime |
| Sequential execution by default, bounded parallelism by policy | Unbounded fan-out |
| A central card authority with fenced host execution | Shared task or queue files between machines |
| Host-reported capacity and state | Central guesses based on lanes or cached flags |
| Evidence with drill-down to logs, diffs, tests, and artifacts | Trusting a completion paragraph on its own |
| Explicit security, review, and release gates | Silent movement toward `main` |
| Portable, repository-readable skills | Workflow knowledge trapped in one CLI |

See [ROADMAP.md](ROADMAP.md) for future direction and [AGENTS.md](AGENTS.md) for
the operational rules contributors and coding agents must follow.

## What you see

The board keeps active work, waiting work, and review handoffs scannable.

![Board overview with tasks grouped by lifecycle lane](docs/assets/images/board-overview.png)

Task detail condenses the latest result while keeping the underlying activity
and evidence reachable.

![Task detail protocol and activity evidence](docs/assets/images/detail-protocol.png)

Git and software changes remain visible beside the agent conversation.

![Task detail focused on git evidence](docs/assets/images/detail-git-focus.png)

Pipeline evidence makes automated steps and the review handoff inspectable.

![Task pipeline and review progression](docs/assets/images/pipeline-page.png)

## Deterministic orchestration over prompt trust

The model classifies and explains its work. Deterministic application code owns
admission, leases, fences, retry budgets, lane changes, evidence gates, and
terminal outcomes. A supervisor can advise or intervene through explicit,
audited paths, but it does not replace the state machine.

The additive `host-orchestrator/v1` contract negotiates compatibility before a
host accepts work. It carries sequenced capacity and state reports, atomic work
permits, a persisted host-local queue, fenced post-processing, and
same-authority reconciliation after a Task Server restart. The legacy claim
path remains available while migration gates are proven. The authority split
and migration order are documented in the
[distributed target architecture](docs/concepts/distributed-agent-studio-target-architecture.md)
and
[ADR-0067](docs/system/architecture/decisions/adr-archive.md#adr-0067---orchestration-is-two-level-central-card-authority-and-host-local-operational-authority-2026-07-22).

## Portable skills, not CLI-local silos

Skills are repository-readable workflows for repeatable specialist work such as
security review, runtime-log analysis, task API operations, and documentation
regeneration. They produce inspectable Markdown, structured findings, tests, or
other durable evidence beside the task. A skill stays useful when the selected
coding CLI changes because the workflow contract is not hidden in one
provider's private configuration.

The catalog and authoring rules live in
[.agents/skills/README.md](.agents/skills/README.md).

## How it's wired

```text
 +--------------------+       HTTPS/API       +----------------------+
 | Agent Studio       | <-------------------> | Task Server          |
 | Angular + thin BFF |                       | cards, policy, leases|
 +--------------------+                       | fences, audit, flows |
                                              +----+-------------+---+
                                                   ^             ^
                                      stage API    |             | host-orchestrator/v1
                                                   v             v
                                      +------------+--+    +-----+---------------+
                                      | Orchestrator  |    | Agent Runner host   |
                                      | Engine        |    | capacity, admission |
                                      | flow stages   |    | queue, repos, CLI   |
                                      +---------------+    | and post-processing |
                                                           +----------+----------+
                                                                      |
                                                                   git origin
```

Studio is the human surface. Task Server is the durable global control plane.
The API-only Orchestrator Engine executes durable control-plane flow stages.
Agent Runner carries the host-local orchestrator and coding execution plane;
remote review remains a separately fenced role over immutable revisions. Git
moves code, not card truth. Closing Studio does not stop an admitted run, and
restarting Task Server does not grant a second host overlapping authority.

## Running

For the default local development surface:

```bash
./api.sh
cd frontend
npm install
npm start
```

Task Server and remote Runner deployment have separate lifecycle and recovery
steps in the [setup documentation](docs/operations/setup/README.md). The
standalone Linux host runbook is
[linux-runner-host.md](docs/operations/setup/linux-runner-host.md).

## Dev vs stable checkout

The active development checkout is a regression target. Stable is the
supervisor seat and is updated only after a verified development batch. Do not
edit the stable checkout from a task run, restart it mid-run, or let a coding
agent execute in a shared main checkout. Coding work uses task worktrees, while
platform-owned git steps integrate and publish it at explicit boundaries.

The complete checkout, runtime, and verification guardrails are in
[AGENTS.md](AGENTS.md).
