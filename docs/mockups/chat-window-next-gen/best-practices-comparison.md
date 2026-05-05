# Chat Best-Practices Comparison

This comparison is the design input for the compact chat-flow revision. GitHub Copilot Chat in VS Code is the strongest reference because it balances a normal chat transcript with agentic tooling, context, edits, and review controls.

## Sources Reviewed

| Product | Official source | Relevant observed pattern |
|---------|-----------------|---------------------------|
| GitHub Copilot Chat in VS Code | https://code.visualstudio.com/docs/copilot/chat/copilot-chat | Multiple chat surfaces, compact transcript, context mentions, model/agent controls, reviewable code changes. |
| VS Code agent tools | https://code.visualstudio.com/docs/copilot/agents/agent-tools | Tools are configurable, approval-aware, grouped by source, and can be reviewed separately from the main answer. |
| GitHub Copilot Docs | https://docs.github.com/en/copilot/how-tos/chat-with-copilot | Chat is a broad workflow surface for coding, debugging, testing, documenting, security, custom agents, and prompt files. |
| Claude Code | https://docs.claude.com/en/docs/claude-code/slash-commands | Slash commands, permissions, model, cost, usage, memory, and compacting are explicit session controls. |
| Gemini Code Assist | https://docs.cloud.google.com/gemini/docs/codeassist/use-agentic-chat-pair-programmer | Agent mode uses project context files, file references, slash commands, tool lists, MCP visibility, and action approvals. |
| Codex | https://developers.openai.com/codex/cloud and https://developers.openai.com/codex/cli | Codex spans app, IDE, CLI, and web surfaces, with review, commands, sandboxing, subagents, and automation as first-class concepts. |

## What VS Code Copilot Gets Right

1. **The chat transcript stays a chat.** Context, tools, and edit review are adjacent controls, not giant cards that break the back-and-forth.
2. **Context is lightweight and explicit.** `#` mentions add files, folders, symbols, codebase, terminal selection, tools, and fetch context. `@` mentions route to participants such as VS Code or terminal.
3. **Agent target and model are controls, not transcript noise.** The user can see how the agent will run without every turn restating it.
4. **Tool and approval detail is inspectable.** VS Code centralizes tool approval management and treats tool result review separately from the visible answer.
5. **Code changes are reviewed where code lives.** Inline diffs, keep/undo controls, staging, discard, and checkpoints avoid stuffing every diff into chat.
6. **Media evidence gets its own browsing surface.** Image carousel support keeps screenshots visible without turning the transcript into a gallery.

## What Claude Code Gets Right

1. **Commands are fast session controls.** `/model`, `/permissions`, `/usage`, `/cost`, `/compact`, `/review`, and custom commands are discoverable and compact.
2. **The terminal transcript can stay terse.** Claude Code does not need a dashboard beside every answer; deeper state is behind commands.
3. **Memory and permissions are explicit.** The user can inspect or change what the agent is allowed to do.

## What Gemini Code Assist Gets Right

1. **Project memory is a file contract.** `GEMINI.md` or `AGENT.md` gives the chat a stable instruction source.
2. **File references are direct.** `@FILENAME` style context keeps the composer compact.
3. **Tool inventory is available through commands.** `/tools` and `/mcp` make capabilities visible without crowding each answer.
4. **Always-allow is treated as risky.** The approval model is surfaced as a trust decision.

## What Codex Gets Right

1. **One agent across multiple surfaces.** App, IDE extension, CLI, and web are different entry points into the same work model.
2. **Review and commands are product concepts.** The chat is tied to review, automation, sandboxing, subagents, and rules rather than being only a plain transcript.
3. **Background work is a different shape.** Long-running agent work needs task artifacts and review state, not only chat bubbles.

## Design Judgment For Agent Task Processor

Agent Task Processor should not imitate a two-person assistant chat because its product has more actors. But it should imitate the compactness of VS Code Copilot Chat:

- The **center column is the chatflow**: human, agent, orchestrator, supervisor, and supporting agents as alternating participant turns.
- The **meta layer docks to the side**: run metrics, token totals, tool families, commits, tests, screenshots, and raw trace filters live in an inspector that can collapse.
- The **inline transcript uses disclosure rows**: tool bursts, decision summaries, artifacts, and warnings are one-line or two-line items until expanded.
- The **composer carries controls**: current chat, model, agent mode, permission level, configuration, context chips, attachments, slash actions, start, stop, and send live at the input edge.
- The **trace is a mode, not the default**: raw log, JSON, and individual tool calls stay one click away.
- The **verbose debug view is separate**: the dashboard-like diagnostic view is still valuable, but it opens as a read-only fullscreen developer surface instead of competing with the normal chat.

## Concrete UI Rules

| Area | Rule |
|------|------|
| Message density | One message should usually fit in 2 to 6 text lines. Long agent answers collapse after the first meaningful paragraph. |
| Actor identity | Use small avatars/initials plus label and role chip. Color is supportive only. |
| Tool use | Show as compact inline rows like `Tools 18 | read 9 | search 5 | edit 2 | shell 2 | 1 failed`. |
| Orchestrator | Show as a slim decision row inline. Expanded view shows reason, evidence, action, budget. |
| Supervisor | Show as advisory row with severity and observed phase. Expanded view shows evidence and suggested action. |
| Metadata | Default to right inspector, collapsible. Never force the user to read metrics before the chat. |
| Run boundaries | Use thin date/run separators, not large cards around every run. |
| Composer | Keep mode, target, context, and slash actions visible as small chips above the input. |
| Screenshots | Show a small artifact chip inline and thumbnail in inspector or artifact view. |
| Raw trace | Keep exact raw entries in Trace mode and expansion panels. |
| Debug view | Provide a fullscreen read-only view for actor counts, duration, tool density, task markers, orchestrator explanations, warnings, and raw trace links. |

## Revised Mockup Direction

The revised mockup should look like a polished developer chat, not an operations dashboard. The transcript remains a normal vertical conversation with compact participant turns. Our special concepts appear as compact inline events:

- `Orchestrator reissued continuation`
- `Tools 96 calls, 1 failed`
- `QA report parsed`
- `Supervisor advisory, medium`
- `Screenshot evidence, 7 files`

The inspector can sit beside the chat when space allows, which was useful in the first mockup. On smaller screens it collapses behind an `Inspector` control.

The first dashboard-style mockup should survive as `Verbose Debug`. That gives developers the richer analysis surface when they need to understand a confusing run, while preserving the compact primary chat for daily use.
