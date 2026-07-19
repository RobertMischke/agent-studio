# Security overview

> **Status note (2026-07-07).** ADR-0059 promotes **remote execution** (Linux
> runner hosts + a central task-server URL) from explicit non-goal to a major
> goal. The "local-only" situation below still describes the **current,
> deployed** state and stays accurate until the remote phases land — but the
> product-thesis framing is superseded, and this document must be rewritten
> with a real threat model (auth on the central URL, SSH-provisioned runner
> hosts, per-runner identities) **before** any port is exposed beyond SSH.
> Plan of record: [concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
> (the central-URL auth boundary and runner split gate the remote phases).
>
> **Target clarification (2026-07-13).** The required human login, Runner
> service identity, HTTPS boundary, audit, and management model is now defined
> in [Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md#8-security-baseline-for-an-internet-reachable-server).
> The local-only text below is current-state documentation, not the networked
> target.

## Situation

Agent Software Studio is a **local-only desktop app** *(current state; see status note above)*. The backend binds to `localhost:5030`, the frontend to `localhost:4010`, and CORS allows only those origins. There is no public network surface.

The app drives external CLI agents (Claude Code, Codex, Copilot, Gemini) as the logged-in user. Anything the user can do at the shell, the agents can do too; the trust boundary is the user's machine, not the app.

## Why it looks like this

The product thesis is "a workbench that keeps one project moving, scaled across many projects" (see [ROADMAP.md](../../../ROADMAP.md)). Multi-tenant deployment and team-shared infrastructure are explicit non-goals. Remote orchestration **was** on that list until 2026-07 — ADR-0059 reverses it; single-user stays, single-machine goes. Treating the app as single-user-on-single-machine collapses most threat-model categories to "the user's account is already root for everything that matters here"; that collapse no longer holds once a central URL exists, which is why the remote plan makes authentication a phase-2 gate.

## Open considerations

- **Task folders are external to the product checkout.** The app reads and
  writes task folders below the configured central `TaskRepository`. Legacy
  watch-path configuration can still point at another directory, so a hostile
  or mistaken configuration could expose unintended paths.
- **CLI quota credentials.** Agents read their own auth state from disk (Claude session files, Copilot tokens). The app never reads or stores those secrets, but bugs in CLI drivers could conceivably leak prompts or session IDs into logs.
- **Markdown rendering.** Frontend renders Markdown for status, prompts, and now these security/architecture docs. The renderer is hand-written and avoids `innerHTML` for user input outside the dedicated editor; review it whenever you touch the markdown utility.

## Surfaced project UI

The project shell now exposes Security, Token Usage, Observability, and Steering Docs as first-class project surfaces. Treat every new project-level surface as part of the security model:

- **Security.** The Security panel reads `security/baseline.md`, `security/reviews/*.md`, and `security/state.json`. The manual audit action creates a normal queued job and the resulting evidence is written back as a review document. It must not run hidden scans or edit source directly.
- **Token Usage.** Token Usage is read-only observability over orchestrator token records. It may show totals, heatmaps, expensive jobs, and drill-downs, but it must not enforce budgets, schedule work, or expose CLI quota credentials.
- **Observability and steering docs.** These surfaces may read structured reports and project documentation. Any action that changes project state must go through the documented task/report contract rather than writing arbitrary watched-project files.
- **Mockup and design evidence.** Standalone mockup apps and screenshots are review artifacts. Generated screenshots and dev-server logs are ignored output, not source or security evidence unless copied intentionally into a task result folder.
