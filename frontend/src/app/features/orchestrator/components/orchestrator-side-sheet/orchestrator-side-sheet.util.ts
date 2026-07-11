import type { OrchestratorChatTurn } from '../../../../features/orchestrator';
import type { ChatEvent, ConversationEvent, RawLineRange } from 'coding-agent-chat/core';

/**
 * Pure helpers for the orchestrator side sheet. Extracted from the
 * component controller so the component .ts stays within its size budget
 * while the navigation-context / pin logic (MC-2) lives inline where it
 * belongs. These functions carry no Angular dependency and are unit-tested
 * directly.
 */

/**
 * Build the sample inline event cards seeded by the `?demoEvents=1` URL
 * flag. Pure data factory extracted from the component so the controller
 * stays within its size budget; `baseTs` is the epoch-ms anchor the caller
 * passes in (typically `Date.now()`) and every card is offset from it so
 * the demo timeline renders in a stable order. Covers the six event kinds
 * (tool-call, watchdog, rate-limit, session-recovered, memory-refreshed,
 * decision) plus the four decision sub-types the chat head row glyphs.
 */
export function buildDemoEvents(baseTs: number): ChatEvent[] {
  const iso = (offsetMs: number) => new Date(baseTs + offsetMs).toISOString();
  return [
    {
      id: 'demo-tool-call-1',
      kind: 'tool-call',
      timestamp: iso(0),
      summary: 'Read backend/Services/Runner/PhaseAwareWatchdog.cs',
      detail:
        '```\n'
        + '/* Result: 412 lines, last modified 2026-05-04 */\n'
        + 'PhaseAwareWatchdog observes per-phase silence budgets;\n'
        + 'FormatBudgetReason emits a one-line summary plus the\n'
        + 'previous CLI event that preceded the silence so the\n'
        + 'operator can see what the agent was doing.\n'
        + '```'
    },
    {
      id: 'demo-watchdog-1',
      kind: 'watchdog',
      timestamp: iso(45_000),
      severity: 'warn',
      summary: 'Tool burst phase silent for 90s (budget: 60s)',
      detail:
        '**Phase:** tool-burst\n\n**Silence:** 90s\n\n**Budget:** 60s\n\n'
        + 'Last event before the silence:\n\n'
        + '```\n'
        + '● Read frontend/src/app/components/chat/chat.component.ts\n'
        + '  L1-100\n'
        + '```'
    },
    {
      id: 'demo-rate-limit-1',
      kind: 'rate-limit',
      timestamp: iso(90_000),
      severity: 'warn',
      summary: 'Anthropic 5h window: 78% used, resets in 1h 12m',
      detail:
        '```\n'
        + '{\n'
        + '  "type": "rate_limit_event",\n'
        + '  "window": "5h",\n'
        + '  "used_pct": 78,\n'
        + '  "reset_at": "2026-05-06T13:12:00Z"\n'
        + '}\n'
        + '```'
    },
    {
      id: 'demo-session-recovered-1',
      kind: 'session-recovered',
      timestamp: iso(120_000),
      summary: '1 turn lost, retry succeeded',
      detail:
        'The model session dropped between the user steer and the\n'
        + 'agent reply (network blip, ~7s). Retry succeeded against\n'
        + 'the same session id; the lost turn was re-issued and the\n'
        + 'agent picked up at the same instruction.'
    },
    {
      id: 'demo-memory-refreshed-1',
      kind: 'memory-refreshed',
      timestamp: iso(150_000),
      summary: 'sourced from 6 status files + roadmap',
      detail:
        '**Sources refreshed:**\n\n'
        + '- `.orchestrator/status/*.md` (6 files)\n'
        + '- `ROADMAP.md`\n\n'
        + 'Working memory updated; the next agent reply will reflect\n'
        + 'the latest project state.'
    },
    // F15: decision-event seeds. Each `decisionType` picks a distinct
    // glyph in the chat head row; covers the four orchestrator-side
    // verdict kinds (decision, reissue, heuristic, giveup).
    {
      id: 'demo-decision-1',
      kind: 'decision',
      decisionType: 'decision',
      timestamp: iso(180_000),
      summary: 'accept-as-done after 1 reissue',
    },
    {
      id: 'demo-decision-2',
      kind: 'decision',
      decisionType: 'reissue',
      timestamp: iso(210_000),
      summary: 'fast NoOp on UserContinue with follow-up; re-issuing once',
    },
    {
      id: 'demo-decision-3',
      kind: 'decision',
      decisionType: 'heuristic',
      timestamp: iso(240_000),
      summary: 'no sentinel matched; falling back to heuristic verdict',
      severity: 'warn',
    },
    {
      id: 'demo-decision-4',
      kind: 'decision',
      decisionType: 'giveup',
      timestamp: iso(270_000),
      summary: 'second reissue produced no progress; asking user',
      severity: 'warn',
    }
  ];
}

/**
 * Hide any server user turn that an in-flight local turn already represents.
 *
 * After the operator hits Send we render a local "optimistic" turn so the
 * bubble shows up immediately (including the inline blob preview of any
 * attached image). When the round-trip to the orchestrator finishes the
 * server now reports the same user turn back, but the local turn is still
 * on screen until the persisted attachment URL has been pre-decoded into
 * the browser image cache. Without this dedup, the user would see the
 * bubble briefly duplicate during that pre-decode window.
 *
 * Match strategy: walk local user turns newest-to-oldest and pair each
 * with the newest unmatched server user turn that has the same text and
 * the same number of attachments. Pairing is greedy and one-shot so
 * sending the same message twice in a row only suppresses one copy per
 * local turn.
 */
export function suppressLocalDuplicates(
  server: readonly OrchestratorChatTurn[],
  local: readonly (OrchestratorChatTurn & { localAttachments?: { alt: string; previewUrl: string }[] })[]
): OrchestratorChatTurn[] {
  if (local.length === 0) return [...server];
  const localUsers = local.filter((t) => t.role === 'user');
  if (localUsers.length === 0) return [...server];
  const suppress = new Set<string>();
  for (const lt of localUsers) {
    const ltAttCount = lt.localAttachments?.length ?? lt.attachments?.length ?? 0;
    for (let i = server.length - 1; i >= 0; i--) {
      const st = server[i];
      if (suppress.has(st.id)) continue;
      if (st.role !== 'user') continue;
      if ((st.text ?? '') !== (lt.text ?? '')) continue;
      const stAttCount = st.attachments?.length ?? 0;
      if (stAttCount !== ltAttCount) continue;
      suppress.add(st.id);
      break;
    }
  }
  return suppress.size === 0 ? [...server] : server.filter((s) => !suppress.has(s.id));
}

export type OptimisticOrchestratorChatTurn = OrchestratorChatTurn & {
  pending?: boolean;
  localAttachments?: { alt: string; previewUrl: string }[];
};

/**
 * Project the side sheet's transport-specific transcript into the canonical
 * `coding-agent-chat` conversation grammar.
 *
 * The orchestrator endpoint returns simple user/orchestrator turns while the
 * optimistic path temporarily holds a second, local representation of the
 * newest user turn. The next-gen conversation view must receive one ordered
 * event stream, so this adapter suppresses that overlap, resolves both local
 * and persisted attachment URLs, and translates the legacy inline event-card
 * contract into the closest semantic `ConversationEvent` kind.
 */
export function buildOrchestratorConversationEvents(
  serverTurns: readonly OrchestratorChatTurn[],
  localTurns: readonly OptimisticOrchestratorChatTurn[],
  inlineEvents: readonly ChatEvent[],
  projectName: string | null,
  source: string,
): ConversationEvent[] {
  const persisted = suppressLocalDuplicates(serverTurns, localTurns);
  const turns: readonly OptimisticOrchestratorChatTurn[] = [...persisted, ...localTurns];
  const projected: { event: ConversationEvent; inputIndex: number }[] = [];

  turns.forEach((turn, index) => {
    const localAttachments = turn.localAttachments?.map(attachment => ({
      alt: attachment.alt,
      url: attachment.previewUrl,
    })) ?? [];
    const persistedAttachments = (turn.attachments ?? []).map(attachment => ({
      alt: attachment.alt,
      url: resolveAttachmentUrl(projectName, attachment.relativePath),
    }));
    const attachments = localAttachments.length > 0 ? localAttachments : persistedAttachments;
    const error = turn.errorMessage?.trim();
    const body = error
      ? `${turn.text ? `${turn.text}\n\n` : ''}**Error:** ${error}`
      : turn.text;

    projected.push({
      inputIndex: index,
      event: {
        id: turn.id,
        kind: turn.role === 'user' ? 'message.user' : 'message.orchestrator',
        timestamp: turn.ts,
        severity: error ? 'error' : undefined,
        model: turn.model ?? turn.tokenUsage?.model ?? null,
        thinkingLevel: turn.tokenUsage?.thinkingLevel ?? null,
        rawRange: rangeFor(source, index),
        body,
        actor: turn.role === 'user' ? 'You' : 'Orchestrator',
      },
    });

    attachments.forEach((attachment, attachmentIndex) => {
      projected.push({
        inputIndex: index + (attachmentIndex + 1) / (attachments.length + 1),
        event: {
          id: `${turn.id}:attachment:${attachmentIndex}`,
          kind: 'artifact.image',
          timestamp: turn.ts,
          rawRange: rangeFor(source, index),
          caption: attachment.alt,
          url: attachment.url,
          sourcePath: attachment.url,
          durablePath: null,
          sourceTool: 'orchestrator-chat',
        },
      });
    });
  });

  inlineEvents.forEach((event, index) => {
    const inputIndex = turns.length + index;
    projected.push({
      inputIndex,
      event: projectInlineEvent(event, rangeFor(source, inputIndex)),
    });
  });

  return projected
    .sort((left, right) => compareTimestamp(left.event.timestamp, right.event.timestamp)
      || left.inputIndex - right.inputIndex)
    .map(item => item.event);
}

function projectInlineEvent(event: ChatEvent, rawRange: RawLineRange): ConversationEvent {
  const base = {
    id: event.id,
    timestamp: event.timestamp,
    severity: event.severity,
    rawRange,
  } as const;

  switch (event.kind) {
    case 'tool-call':
      return {
        ...base,
        kind: 'toolBurst',
        count: 1,
        families: { other: 1 },
        failures: event.severity === 'error' ? 1 : 0,
        durationMs: 0,
        samples: { other: event.summary },
        collapsedByDefault: true,
      };
    case 'watchdog':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: event.severity === 'error' || /\bkill(?:ed)?\b/i.test(event.summary) ? 'killed' : 'quiet',
        quietSeconds: secondsFrom(event.summary),
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'session-recovered':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: 'resumed',
        quietSeconds: 0,
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'decision':
      return {
        ...base,
        kind: 'decision.orchestrator',
        decisionType: event.decisionType ?? 'decision',
        reason: event.summary,
        evidence: event.detail,
        action: event.actionLabel,
      };
    case 'rate-limit':
    case 'update':
    case 'task':
    case 'memory-refreshed':
      return {
        ...base,
        kind: 'system.status',
        category: event.kind,
        label: inlineEventLabel(event.kind),
        explanation: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
        nextStep: event.actionLabel,
      };
  }
}

function rangeFor(source: string, index: number): RawLineRange {
  const line = index + 1;
  return { source, start: line, end: line };
}

function compareTimestamp(left: string, right: string): number {
  const leftMs = Date.parse(left);
  const rightMs = Date.parse(right);
  if (!Number.isFinite(leftMs) || !Number.isFinite(rightMs)) return 0;
  return leftMs - rightMs;
}

function secondsFrom(summary: string): number {
  const match = /\b(\d+)\s*s(?:ec(?:ond)?s?)?\b/i.exec(summary);
  return match ? Number(match[1]) : 0;
}

function inlineEventLabel(kind: Extract<ChatEvent['kind'], 'rate-limit' | 'update' | 'task' | 'memory-refreshed'>): string {
  switch (kind) {
    case 'rate-limit': return 'Rate limit';
    case 'update': return 'Update';
    case 'task': return 'Task';
    case 'memory-refreshed': return 'Memory refreshed';
  }
}

/**
 * Slice E: parse `#tag1 #tag2` patterns at the start of any line in the
 * `/bug` description. A tag word is `[A-Za-z][\w-]*`; a leading `# ` (with
 * a space) is treated as Markdown heading syntax and skipped, so the
 * common case where the user opens the description with a heading does
 * not capture the heading text as a tag.
 */
export function parseBugHashtags(description: string): string[] {
  const found: string[] = [];
  for (const line of description.split('\n')) {
    const trimmed = line.trim();
    if (!/^#[A-Za-z]/.test(trimmed)) continue;
    const matches = trimmed.match(/#[A-Za-z][\w-]*/g);
    if (!matches) continue;
    for (const m of matches) {
      const tag = m.substring(1);
      if (!found.includes(tag)) found.push(tag);
    }
  }
  return found;
}

/**
 * Resolve a persisted chat attachment's relative path to the GET endpoint
 * that actually serves the bytes. Server returns `chat-attachments/<file>`;
 * we strip that prefix and route through the per-project attachments route
 * so the `<img>` in the bubble loads. Returns the input unchanged when the
 * project or path is missing.
 */
export function resolveAttachmentUrl(projectName: string | null, relativePath: string): string {
  if (!projectName || !relativePath) return relativePath;
  const fileName = relativePath.startsWith('chat-attachments/')
    ? relativePath.substring('chat-attachments/'.length)
    : relativePath;
  return `/api/runner/${encodeURIComponent(projectName)}/orchestrator-chat/attachments/${encodeURIComponent(fileName)}`;
}

/**
 * Read a pasted/dropped file as a base64 payload for the multimodal fast
 * path. Strips the `data:<mime>;base64,` prefix so the backend only sees
 * the raw base64. Files larger than 10 MB resolve to null so the inline
 * path is skipped and the chat falls back to the archived-only behaviour
 * (matches the backend upload cap).
 */
export function readFileAsBase64(file: File): Promise<{ base64: string; mimeType: string } | null> {
  return new Promise((resolve) => {
    if (file.size > 10 * 1024 * 1024) {
      resolve(null);
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const result = typeof reader.result === 'string' ? reader.result : '';
      const comma = result.indexOf(',');
      const base64 = comma >= 0 ? result.substring(comma + 1) : result;
      const mimeMatch = /^data:([^;]+);base64,/.exec(result);
      const mimeType = mimeMatch?.[1] ?? file.type ?? 'image/png';
      resolve({ base64, mimeType });
    };
    reader.onerror = () => resolve(null);
    reader.readAsDataURL(file);
  });
}
