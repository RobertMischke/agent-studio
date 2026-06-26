# Security requirements

These are the load-bearing constraints. If you make a change that touches one, surface it in the PR description.

## R1 - Localhost only

Backend binds `127.0.0.1:5030`. Frontend dev server binds `127.0.0.1:4010`. CORS restricted to those two origins. No `0.0.0.0`, no LAN listeners, no tunneling.

## R2 - No secret material in the repo

Auth tokens, CLI sessions, quota credentials live in user-scoped paths (Claude `~/.claude`, Copilot, Gemini), never in this tree. `appsettings.Local.json` is gitignored and may contain absolute machine paths, but no secrets.

## R3 - Watched target writes go through the contract

The app never edits a watched project's source code directly. All writes that affect the target repo go through the agent (which the user supervises) or through the app-owned task lifecycle (`docs/contracts/agent-task.md`).

## R4 - Markdown rendering is read-only and inert

User-authored markdown (prompts, status files, security/architecture docs) is rendered through `markdown-utils.ts`. The renderer escapes raw HTML; only headings, paragraphs, lists, code, links, images, bold, and italic are emitted. No script execution, no inline event handlers.

## R5 - Project-level surfaces are explicit read or trigger surfaces

Project shell panels may surface Security, Token Usage, Observability, Steering Docs, Analysis Reports, Drift, and future QA/UX views, but each panel must declare whether it is read-only or action-triggering. Read-only panels must not mutate job state, source files, runner mode, or budgets. Action-triggering panels must create a normal queued job, write a structured report through the project evidence contract, or call an existing documented endpoint with visible feedback.

## R6 - Security and analysis actions leave durable evidence

Manual security audits, roadmap checks, docs-drift checks, token-spend reviews, and observability analyses must leave durable evidence: Markdown for humans plus structured JSON when the UI aggregates it. A panel button must not silently run an agent and only show an ephemeral toast. The minimum record is action, project, requested scope, timestamp, model or CLI when known, token usage when known, evidence path, result, and follow-up job id if one was created.

## R7 - Token and quota surfaces never expose credentials

Token Usage, CLI Usage, quota strips, heatmaps, and status-bar usage pills may display aggregate counts, percentages, reset windows, model names, job ids, and run metadata. They must not render auth tokens, session secret material, raw vendor credential files, or user-scoped credential paths beyond coarse source labels such as "Claude session files" or "Copilot token state". Token totals are observability, not permission or scheduling enforcement.
