# Next-Generation Chat Window Research

This research combines Stable observations from Playwright with official external product documentation. It focuses on interaction patterns, not visual cloning.

## Stable Observation

Stable was inspected through Playwright on May 5, 2026. The target was the Stable frontend on port `4011`, backed by the Stable backend on port `5031`.

The existing E2E specs `activity-log-chat-mode.spec.ts` and `activity-log-tool-chips.spec.ts` currently fail on Stable because the `activity-log-mode-conversation` button is visible but pointer events are intercepted by neighboring layout layers. The failing screenshots show run-timeline, auto-eval, and activity-panel surfaces competing for the same space.

Additional Playwright sampling opened three already-run jobs:

| Job | State | Turns | Tool pills | Tool chips |
|-----|-------|-------|------------|------------|
| `chat-read--grep-wiederholunge-mit-weight-darstellen` | `6-archive` | 245 | 114 | 148 |
| `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` | `4-review` | 199 | 93 | 124 |
| `project-analysis-reports-surface` | `4-review` | 82 | 39 | 60 |

Conclusion: the current UI already handles logs large enough that raw chip rendering becomes the product's bottleneck. The next UI should treat tool use as a summarized event group by default.

## External Product Patterns

### OpenAI Codex

OpenAI describes Codex as a coding agent that can write, review, and ship code, with surfaces across app, IDE extension, CLI, and web. Codex Cloud tasks run in sandboxed environments, and Codex is positioned for background and multi-agent coding workflows.

Relevant pattern: separate the agent work surface from the underlying execution environment, and make background work inspectable through task-level artifacts.

Sources:

- https://openai.com/codex/
- https://platform.openai.com/docs/codex
- https://help.openai.com/en/articles/11096431
- https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan

### Claude Code

Claude Code exposes slash commands, settings, tool permissions, and permission modes. Official docs emphasize that tool use and command permissions are explicit operating concepts, not hidden implementation details.

Relevant pattern: a chat-like agent loop needs visible permissions, tool actions, and command context. For this product, those map to tool bursts, actor rails, and decision cards.

Sources:

- https://docs.claude.com/en/docs/claude-code/slash-commands
- https://docs.claude.com/en/docs/claude-code/settings
- https://docs.claude.com/en/docs/claude-code/iam
- https://code.claude.com/docs/en/agent-sdk/permissions

### GitHub Copilot Chat and VS Code Agent Mode

GitHub and VS Code describe chat as multiple surfaces: chat view, inline chat, quick chat, and command-line chat. VS Code agent mode includes agent target, agent picker, permission level, model selection, context mentions, image context, tool configuration, checkpoints, diffs, and an Agent Logs view for chronological tool calls, LLM requests, and prompt discovery.

Relevant pattern: the conversation is the human-friendly layer; agent logs and tool traces remain separate but reachable.

Sources:

- https://docs.github.com/copilot/using-github-copilot/copilot-chat
- https://docs.github.com/en/copilot/get-started/features
- https://docs.github.com/copilot/concepts/about-copilot-coding-agent
- https://code.visualstudio.com/docs/copilot/chat/chat-agent-mode
- https://code.visualstudio.com/docs/copilot/agents/agent-tools

### Gemini Code Assist

Gemini Code Assist supports multiple chats, editing prior prompts, regenerating responses, deleting prompt/response pairs, selected code and terminal context, code preview/diffs, stopping in-progress responses, and model selection. Agent mode adds plans, built-in tools, MCP tools, permission checks, context drawers, and user approval for mutating operations.

Relevant pattern: context, plans, permissions, and tool execution are first-class chat-adjacent controls.

Sources:

- https://cloud.google.com/gemini/docs/codeassist/chat-overview
- https://cloud.google.com/gemini/docs/codeassist/use-gemini-code-assist-chat
- https://cloud.google.com/gemini/docs/codeassist/agent-mode
- https://cloud.google.com/gemini/docs/codeassist/use-agentic-chat-pair-programmer

## Product Differentiation

Agent Task Processor should not copy any one vendor chat. Its differentiator is that several actors share one durable project transcript:

- The user provides intent and review.
- The task agent changes the software.
- The orchestrator interprets outcome and may reissue work.
- The supervisor watches health and risk.
- Supporting agents produce QA, design, security, drift, and meta-analysis evidence.
- Tool runners create low-level evidence.

This means the best UI is not a simple two-party chat. It is a conversation timeline with actor grammar, run grouping, and expandable trace evidence.

## Design Implications

1. Conversation mode should be the default for review.
2. Trace mode should be optimized for debugging and can remain dense.
3. Tool calls should collapse into grouped bursts with counts, failures, duration, and artifacts.
4. Decisions should render as cards with reason, evidence, action, and budget.
5. Context should be visible near the composer: current run, target actor, attached files, mode, and follow-up intent.
6. Failed schema parsing or weak log parsing should surface as a system warning inside the stream, not as a silent formatting collapse.

