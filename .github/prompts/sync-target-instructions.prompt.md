---
description: "Synchronize Copilot instructions to a watched target project. Use when: the job folder contract, states, or autopilot workflow changed and dependent projects need updated instructions."
agent: "agent"
---

# Sync Target Instructions

Read the current system contract from these files:
- [copilot-instructions.md](../copilot-instructions.md) — current project guidelines
- [filesystem-contract.md](../../docs/filesystem-contract.md) — job folder schema and states

Then read the backend config to find all configured watch paths:
- [appsettings.Development.json](../../backend/appsettings.Development.json)

For **each watch path**, determine the parent project root (two levels above `.orchestrator/jobs/`).

Create or update a `.github/copilot-instructions.md` in that project root with Copilot instructions that describe the **autopilot workflow**:

## Required content in the target instructions

The generated instructions MUST include these sections:

### 1. Job Orchestration
Explain that `.orchestrator/jobs/` contains numbered state folders (`1-preparation`, `2-ready`, `3-progress`, `4-review`, `5-completed`) and that the agent should look for jobs in `2-ready/` to pick up work.

### 2. Autopilot Workflow
Step-by-step what Copilot should do when running in autopilot mode:
1. Scan `.orchestrator/jobs/2-ready/` for the next job folder
2. Read `prompt.md` for the task description
3. Move the job folder to `3-progress/` (physically move the directory)
4. Update `status.md` with progress notes as work proceeds
5. When done, move the job folder to `4-review/`
6. If no jobs are in `2-ready/`, stop — do not pick from other state folders

### 3. Job File Contract
Describe the required files per job folder:
- `job.json` — metadata (do not modify `id` or `createdAt`)
- `prompt.md` — read-only task description
- `status.md` — agent updates this with a processing protocol
- `logs/` — optional, place build outputs or logs here

### 4. Rules
- Only pick jobs from `2-ready/`
- Never modify `prompt.md`
- Always update `status.md` before and after work
- Work in the project source tree, not inside the job folder
- Keep `status.md` concise — bullet points, not essays

## Important
- Do NOT overwrite existing non-orchestrator sections in the target project's `copilot-instructions.md`. Append or merge the orchestrator section.
- Mark the generated section with a clear header like `## Orchestrator — Autopilot Workflow` so it can be identified and updated later.
- If the target `.github/` folder doesn't exist, create it.
