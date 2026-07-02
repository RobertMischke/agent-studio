import { ChangeDetectionStrategy, Component, OnDestroy, computed, signal } from '@angular/core';

import { ConversationViewComponent } from '@coding-agent/chat/conversation';
import type { ConversationEvent } from '@coding-agent/chat/core';

/**
 * Standalone screenshot harness for the real `ConversationViewComponent`.
 *
 * It renders the production component (not a hand-rolled prototype) against a
 * hand-built `ConversationEvent[]` so the visual overhaul — single continuous
 * surface, role-transition headers, the tool-activity toggle, and the session
 * init / rate-limit meta card — can be captured exactly as the Activity tab
 * paints it, with the real semantic studio tokens loaded globally.
 */
const SESSION_ID = '7f3a9c21-4b2e-4c1a-9f3d-2a1b6c7d8e90';
const RESET_SECS = Math.floor((Date.now() + 4.4 * 3600 * 1000) / 1000);

function isoAt(offsetSec: number): string {
  const base = Date.now() - 11 * 60 * 1000;
  return new Date(base + offsetSec * 1000).toISOString();
}

function range(start: number, end: number): { source: string; start: number; end: number } {
  return { source: 'cli-output.log', start, end };
}

const EVENTS: readonly ConversationEvent[] = [
  {
    id: 'm-user-1',
    kind: 'message.user',
    timestamp: isoAt(0),
    actor: 'You',
    body: 'Investigate the failing protocol parser regression and add a guard so the sentinel grammar stops slipping through.',
    rawRange: range(1, 2),
  },
  {
    id: 'm-init',
    kind: 'message.taskAgent',
    timestamp: isoAt(4),
    actor: 'Agent',
    body: `● Session init ${SESSION_ID}`,
    rawRange: range(3, 3),
  },
  {
    id: 'm-rate',
    kind: 'message.taskAgent',
    timestamp: isoAt(5),
    actor: 'Agent',
    body: `● Rate limit · five-hour · allowed · reset in 4.4 h  [window=five_hour status=allowed resetsAt=${RESET_SECS} overage=allowed usingOverage=false]`,
    rawRange: range(4, 4),
  },
  {
    id: 'm-agent-1',
    kind: 'message.taskAgent',
    timestamp: isoAt(7),
    actor: 'Agent',
    body: 'Reading `prompt.md` and the parser to understand how the sentinel grammar is matched.',
    rawRange: range(5, 6),
  },
  {
    id: 'm-agent-2',
    kind: 'message.taskAgent',
    timestamp: isoAt(9),
    actor: 'Agent',
    body: 'The heuristic outcome short-circuits before the trailing marker is checked, so a malformed sentinel is treated as a pass.',
    rawRange: range(7, 8),
  },
  {
    id: 'burst-1',
    kind: 'toolBurst',
    timestamp: isoAt(12),
    collapsedByDefault: true,
    count: 9,
    families: { read: 3, search: 2, edit: 2, command: 2 },
    failures: 0,
    durationMs: 42_000,
    files: ['src/components/activity-log.parser.ts', 'src/components/activity-log.parser.spec.ts'],
    tests: [{ command: 'npm --prefix frontend run test', status: 'pass' }],
    samples: {
      read: 'Read activity-log.parser.ts (1-180)',
      search: 'Search "sentinel"',
      edit: 'Edit activity-log.parser.ts — add heuristic guard',
      command: 'Run npm --prefix frontend run test → exit 0',
    },
    rawRange: range(9, 21),
  },
  {
    id: 'm-agent-3',
    kind: 'message.taskAgent',
    timestamp: isoAt(58),
    actor: 'Agent',
    body: 'Added the guard and a regression test. The parser now rejects the malformed sentinel and the suite is green (108 passed).',
    rawRange: range(22, 24),
  },
  {
    id: 'decision-1',
    kind: 'decision.orchestrator',
    timestamp: isoAt(60),
    decisionType: 'decision',
    reason: 'Sentinel grammar satisfied and tests pass — no reissue needed.',
    action: 'complete',
    retryBudget: { used: 0, max: 3 },
    rawRange: range(25, 26),
  },
  {
    id: 'm-user-2',
    kind: 'message.user',
    timestamp: isoAt(64),
    actor: 'You',
    body: 'Looks good — please also add a CHANGELOG entry under Unreleased.',
    rawRange: range(27, 27),
  },
  {
    id: 'm-agent-4',
    kind: 'message.taskAgent',
    timestamp: isoAt(70),
    actor: 'Agent',
    body: 'Added a CHANGELOG entry under Unreleased noting the sentinel-guard fix.',
    rawRange: range(28, 29),
  },
];

@Component({
  selector: 'mockup-conversation-view-harness',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConversationViewComponent],
  templateUrl: './conversation-view-harness.component.html',
  styleUrl: './conversation-view-harness.component.scss',
})
export class ConversationViewHarnessComponent implements OnDestroy {
  readonly baseEvents = EVENTS;
  readonly extraEvents = signal<readonly ConversationEvent[]>([]);
  readonly status = signal<'idle' | 'queued' | 'working'>('idle');
  readonly draft = signal('');
  readonly streaming = signal(false);
  private streamTimer: ReturnType<typeof setInterval> | null = null;

  readonly events = computed(() => [...this.baseEvents, ...this.extraEvents()]);

  setStatus(status: 'idle' | 'queued' | 'working'): void {
    this.status.set(status);
  }

  onDraft(value: string): void {
    this.draft.set(value);
  }

  appendEvent(): void {
    const next = this.extraEvents().length + 1;
    this.extraEvents.update((events) => [
      ...events,
      {
        id: `stream-${next}`,
        kind: 'message.taskAgent',
        timestamp: new Date().toISOString(),
        actor: 'Agent',
        body: `Streaming update ${next}: still working while the composer keeps focus.`,
        rawRange: range(40 + next, 40 + next),
      },
    ]);
  }

  toggleStream(): void {
    if (this.streaming()) {
      this.stopStream();
      return;
    }
    this.streaming.set(true);
    this.streamTimer = setInterval(() => this.appendEvent(), 700);
  }

  private stopStream(): void {
    this.streaming.set(false);
    if (this.streamTimer !== null) {
      clearInterval(this.streamTimer);
      this.streamTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.stopStream();
  }
}
