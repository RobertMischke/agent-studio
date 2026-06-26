# CLI skill files

This folder holds the operational reference for each coding-agent CLI the task processor drives. They are **required reading** when touching a CLI driver, and they are **shared across CLIs**: any agent driving this repo (Claude Code, Codex, Gemini) is expected to load the matching skill before changing CLI integration code.

These files are the current proto-skill set. The broader portable skill architecture is documented in [../skills-architecture.md](../../product/skills-architecture.md): long term, standard and project-specific skills should live in the task processor's central skill library, with watched projects exposing a README lookup section so direct CLI sessions can discover them too. Until that migration exists, `docs/cli/skills/` remains the active source for CLI-driver operational skills.

## Index

| Skill | Use when … |
|---|---|
| [cli-overview](./cli-overview.md) | Touching anything in `backend/Services/Cli/`, the activity-log parser, or anything that consumes CLI output. Read alongside the per-CLI skill below. |
| [cli-claude](./cli-claude.md) | Touching `ClaudeCliService`, the stream-json parser, the rate-limit pill, `ClaudeQuotaProbe`. |
| [cli-codex](./cli-codex.md) | Touching `CodexCliService`, the `--json` parser, `CodexModelDiscovery`, `CodexQuotaProbe`. |
| [cli-gemini](./cli-gemini.md) | Touching `GeminiCliService`, the Gemini stream-json parser, the buffered-stdout limitation, `GeminiQuotaProbe`. |
| [sandbox-and-yolo](./sandbox-and-yolo.md) | Touching the permission/sandbox flags any driver injects on spawn, the per-project CLI-mode surface, or the `effective-mode` probe. The mode → flags table and `source` semantics live here. |

## How the skills interact with the contract

[`docs/cli/supported-clis.md`](../supported-clis.md) is the **contract**: what every supported CLI must satisfy. The skills are the **working notes**: how each one actually behaves, the frame catalogues, the capture flows, the known incidents, the common-task playbooks. The two stay in sync — when you change one, change the other in the same PR.

## Sentinel mechanism

Each skill carries a unique sentinel string in its YAML frontmatter (`sentinel: TASKBOARD-CLI-SKILL-<NAME>-2026`) and embedded in the body. Two tests guarantee these sentinels stay valid:

1. **Scaffolding lock** — [`backend.Tests/CliSkillFilesTests.cs`](../../../backend.Tests/CliSkillFilesTests.cs). Walks `docs/cli/skills/`, asserts every expected file exists, every file has the frontmatter and a unique sentinel, and the body echoes the sentinel back. Free; runs on every backend test pass.
2. **Live pickup** — [`frontend/e2e/cli-skills-pickup.spec.ts`](../../../frontend/e2e/cli-skills-pickup.spec.ts) `@billable`. Spawns a tiny job per CLI ("Read this file, find the sentinel, echo it back"), drives the live CLI through the same code path the task processor uses, and asserts the sentinel comes back in the run output. **Opt-in** via `RUN_CLI_PICKUP=1` because environment sensitivity (model availability, transient model behaviours like Haiku replying with an acknowledgement) makes it unreliable as a green-CI gate. Self-skips per CLI when the CLI lacks quota, is unavailable, or produces an empty agent reply (a "model unhealthy" diagnostic rather than a hard fail).

The sentinel is the cheap-to-prove invariant: if the live test passes for every CLI, every CLI demonstrably can find and read its skill file when running on this repo. That's the property the user actually wants the skills to have.

## When you write a new skill

1. Frontmatter fields: `name` (= filename without extension), `description` (detailed enough that a search query like "Codex session capture" surfaces it), `sentinel` (`TASKBOARD-CLI-SKILL-<NAME>-2026` or successor year).
2. Echo the sentinel string in the body once (a comment line is fine).
3. Add the new filename to `ExpectedSkills` in `backend.Tests/CliSkillFilesTests.cs`.
4. Add a `SkillCase` entry to `frontend/e2e/cli-skills-pickup.spec.ts` so the live pickup test covers the new skill too.
5. Update this README's index table.
6. Update [`docs/cli/supported-clis.md`](../supported-clis.md)'s skill-pointer paragraph.
