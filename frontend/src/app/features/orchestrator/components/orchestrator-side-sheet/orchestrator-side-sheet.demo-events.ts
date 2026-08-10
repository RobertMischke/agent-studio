import type { ChatEvent } from 'coding-agent-chat/core';

/** Demo-only event data, lazy-loaded when the explicit URL flag is present. */
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
