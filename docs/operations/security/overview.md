# Security overview

## Situation

Agent Software Studio is a **local-only desktop app**. The backend binds to `localhost:5030`, the frontend to `localhost:4010`, and CORS allows only those origins. There is no public network surface.

The app drives external CLI agents (Claude Code, Codex, Copilot, Gemini) as the logged-in user. Anything the user can do at the shell, the agents can do too; the trust boundary is the user's machine, not the app.

## Why it looks like this

The product thesis is "a workbench that keeps one project moving, scaled across many projects" (see [ROADMAP.md](../../../ROADMAP.md)). Multi-tenant deployment, remote orchestration, and team-shared infrastructure are explicit non-goals. Treating the app as single-user-on-single-machine collapses most threat-model categories to "the user's account is already root for everything that matters here".

## Open considerations

- **Job folders are external.** The app reads/writes job folders inside watched targets. Those watched paths are configured by the user; a hostile config could point at unintended directories.
- **CLI quota credentials.** Agents read their own auth state from disk (Claude session files, Copilot tokens). The app never reads or stores those secrets, but bugs in CLI drivers could conceivably leak prompts or session IDs into logs.
- **Markdown rendering.** Frontend renders Markdown for status, prompts, and now these security/architecture docs. The renderer is hand-written and avoids `innerHTML` for user input outside the dedicated editor; review it whenever you touch the markdown utility.

## Surfaced project UI

The project shell now exposes Security, Token Usage, Observability, and Steering Docs as first-class project surfaces. Treat every new project-level surface as part of the security model:

- **Security.** The Security panel reads `security/baseline.md`, `security/reviews/*.md`, and `security/state.json`. The manual audit action creates a normal queued job and the resulting evidence is written back as a review document. It must not run hidden scans or edit source directly.
- **Token Usage.** Token Usage is read-only observability over orchestrator token records. It may show totals, heatmaps, expensive jobs, and drill-downs, but it must not enforce budgets, schedule work, or expose CLI quota credentials.
- **Observability and steering docs.** These surfaces may read structured reports and project documentation. Any action that changes project state must go through the documented task/report contract rather than writing arbitrary watched-project files.
- **Mockup and design evidence.** Standalone mockup apps and screenshots are review artifacts. Generated screenshots and dev-server logs are ignored output, not source or security evidence unless copied intentionally into a task result folder.
