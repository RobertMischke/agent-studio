# Security requirements

These are the load-bearing constraints. If you make a change that touches one, surface it in the PR description.

## R1 — Localhost only

Backend binds `127.0.0.1:5030`. Frontend dev server binds `127.0.0.1:4010`. CORS restricted to those two origins. No `0.0.0.0`, no LAN listeners, no tunneling.

## R2 — No secret material in the repo

Auth tokens, CLI sessions, quota credentials live in user-scoped paths (Claude `~/.claude`, Copilot, Gemini), never in this tree. `appsettings.Local.json` is gitignored and may contain absolute machine paths, but no secrets.

## R3 — Watched target writes go through the contract

The app never edits a watched project's source code directly. All writes that affect the target repo go through the agent (which the user supervises) or through the app-owned task lifecycle (`docs/agent-task-contract.md`).

## R4 — Markdown rendering is read-only and inert

User-authored markdown (prompts, status files, security/architecture docs) is rendered through `markdown-utils.ts`. The renderer escapes raw HTML; only headings, paragraphs, lists, code, links, images, bold, and italic are emitted. No script execution, no inline event handlers.
