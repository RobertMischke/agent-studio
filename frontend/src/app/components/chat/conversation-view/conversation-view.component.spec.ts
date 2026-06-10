import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ConversationViewComponent } from './conversation-view.component';
import type {
  ConversationEvent,
  FeedbackQueuedEvent,
  MessageEvent,
  RawLineRange,
  RunMarkerEvent,
  ToolBurstEvent,
} from '../conversation-event';

function range(start = 1, end = 1): RawLineRange {
  return { source: 'cli-output.log', start, end };
}

function userMessage(): MessageEvent {
  return {
    id: 'evt-1',
    kind: 'message.user',
    timestamp: '2026-05-05T12:00:00.000Z',
    actor: 'You',
    body: 'Please add a feature flag for NextGenChat.',
    rawRange: range(1, 1),
  };
}

function agentMessage(): MessageEvent {
  return {
    id: 'evt-2',
    kind: 'message.taskAgent',
    timestamp: '2026-05-05T12:00:05.000Z',
    actor: 'Agent',
    body: 'I will add a `Frontend:NextGenChat` flag and the projection scaffold next.',
    rawRange: range(2, 2),
  };
}

function toolBurst(): ToolBurstEvent {
  return {
    id: 'evt-3',
    kind: 'toolBurst',
    timestamp: '2026-05-05T12:00:10.000Z',
    rawRange: range(3, 12),
    count: 4,
    families: { read: 3, edit: 1 },
    failures: 0,
    durationMs: 4_200,
    files: ['feature-flags.service.ts'],
    collapsedByDefault: true,
  };
}

function commandToolBurst(): ToolBurstEvent {
  return {
    id: 'evt-command',
    kind: 'toolBurst',
    timestamp: '2026-05-05T12:00:10.000Z',
    rawRange: range(20, 35),
    count: 1,
    families: { command: 1 },
    failures: 0,
    durationMs: 1_500,
    collapsedByDefault: true,
    commands: [
      {
        command: 'rg -n "needle" frontend/src/app',
        status: 'completed',
        exitCode: 0,
        output: 'frontend/src/app/a.ts:12:const needle = true;',
        outputLineCount: 1,
        outputTruncated: false,
        hits: [{ path: 'frontend/src/app/a.ts', line: 12, text: 'const needle = true;' }],
      },
    ],
  };
}

function agentMsg(id: string, secondsOffset: number, body: string): MessageEvent {
  return {
    id,
    kind: 'message.taskAgent',
    timestamp: new Date(Date.UTC(2026, 4, 5, 12, 0, secondsOffset)).toISOString(),
    actor: 'Agent',
    body,
    rawRange: range(secondsOffset + 1, secondsOffset + 1),
  };
}

function runMarkerStart(id: string, sessionId: string, secondsOffset: number): RunMarkerEvent {
  return {
    id,
    kind: 'runMarker',
    timestamp: new Date(Date.UTC(2026, 4, 5, 12, 0, secondsOffset)).toISOString(),
    marker: 'start',
    sessionId,
    rawRange: range(secondsOffset + 1, secondsOffset + 1),
  };
}

async function makeFixture(events: ConversationEvent[]) {
  await TestBed.configureTestingModule({
    imports: [ConversationViewComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ConversationViewComponent);
  fixture.componentRef.setInput('events', events);
  fixture.componentRef.setInput('isRunning', false);
  fixture.detectChanges();
  return fixture;
}

async function makeFixtureWithState(events: ConversationEvent[], state: { isRunning?: boolean; queuedFollowUp?: boolean }) {
  const fixture = await makeFixture(events);
  fixture.componentRef.setInput('isRunning', state.isRunning ?? false);
  fixture.componentRef.setInput('queuedFollowUp', state.queuedFollowUp ?? false);
  fixture.detectChanges();
  return fixture;
}

describe('ConversationViewComponent', () => {
  it('renders an empty placeholder when there are no events', async () => {
    const fixture = await makeFixture([]);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-empty"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="conversation-feed"]')).toBeFalsy();
  });

  it('renders user and agent messages with actor labels', async () => {
    const fixture = await makeFixture([userMessage(), agentMessage()]);
    const el: HTMLElement = fixture.nativeElement;
    const userRow = el.querySelector(
      '[data-testid="conversation-message-message.user"]'
    );
    const agentRow = el.querySelector(
      '[data-testid="conversation-message-message.taskAgent"]'
    );
    expect(userRow?.textContent).toContain('You');
    expect(userRow?.textContent).toContain('feature flag');
    expect(agentRow?.textContent).toContain('Agent');
    expect(agentRow?.textContent).toContain('NextGenChat');
  });

  it('hosts a tool-burst chip for toolBurst events', async () => {
    const fixture = await makeFixture([toolBurst()]);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-tool-burst"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tool-burst-chip"]')).toBeTruthy();
  });

  it('opens Trace when a command-output file hit is clicked', async () => {
    const event = commandToolBurst();
    const fixture = await makeFixture([event]);
    const el: HTMLElement = fixture.nativeElement;
    const traceEmissions: (RawLineRange | null)[] = [];
    const sourceEmissions: unknown[] = [];
    fixture.componentInstance.openTrace.subscribe((r) => traceEmissions.push(r));
    fixture.componentInstance.openSourceLocation.subscribe((hit) => sourceEmissions.push(hit));

    el.querySelector<HTMLButtonElement>('[data-testid="tool-burst-row"]')?.click();
    fixture.detectChanges();
    el.querySelector<HTMLButtonElement>('[data-testid="tool-burst-hit-path"]')?.click();

    expect(traceEmissions).toEqual([event.rawRange]);
    expect(sourceEmissions).toEqual([
      { path: 'frontend/src/app/a.ts', line: 12, text: 'const needle = true;', rawRange: event.rawRange },
    ]);
  });

  it('renders typed system status and parser-warning rows without raw-frame chrome', async () => {
    const events: ConversationEvent[] = [
      {
        id: 'status-1',
        kind: 'system.status',
        timestamp: '2026-05-05T12:00:20.000Z',
        rawRange: range(40, 40),
        severity: 'warn',
        category: 'codex-silent-completion',
        label: 'Silent completion recovery',
        explanation: 'Codex stopped after final tool call.',
        nextStep: 'Review the result evidence.',
      },
      {
        id: 'warn-1',
        kind: 'system.parserWarning',
        timestamp: '2026-05-05T12:00:21.000Z',
        rawRange: range(41, 41),
        severity: 'warn',
        expectedKind: 'tool-result',
        message: 'Tool router reported exit code 1.',
        dedupeKey: 'tool-router:1',
      },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;

    const status = el.querySelector('[data-testid="conversation-system-status"]');
    expect(status?.getAttribute('data-category')).toBe('codex-silent-completion');
    expect(status?.textContent).toContain('Silent completion recovery');
    expect(status?.textContent).toContain('Review the result evidence.');
    expect(status?.textContent).not.toContain('[codex-silent-completion]');

    const warning = el.querySelector('[data-testid="conversation-parser-warning"]');
    expect(warning?.textContent).toContain('Tool router reported exit code 1.');
    expect(warning?.textContent).toContain('expected: tool-result');
    expect(warning?.textContent).not.toContain('codex_core::tools::router');
  });

  it('renders a recovery status as one calm info row (category + severity hooks, no next-step)', async () => {
    const events: ConversationEvent[] = [
      {
        id: 'recovery-1',
        kind: 'system.status',
        timestamp: '2026-05-05T12:00:20.000Z',
        rawRange: range(40, 40),
        severity: 'info',
        category: 'recovery',
        label: 'Recovery',
        explanation: 'watchdog: silence timeout -> reissue (attempt 1/2, session resumed)',
      },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;

    const status = el.querySelector('[data-testid="conversation-system-status"]');
    // The central-token recovery styling keys off these two attributes.
    expect(status?.getAttribute('data-category')).toBe('recovery');
    expect(status?.getAttribute('data-severity')).toBe('info');
    expect(status?.textContent).toContain('Recovery');
    expect(status?.textContent).toContain('watchdog: silence timeout -> reissue');
    // No escalating next-step span on a calm recovery line.
    expect(status?.querySelector('.status-row__next')).toBeNull();
  });

  it('emits openTrace when the user clicks the Trace header button', async () => {
    const fixture = await makeFixture([userMessage()]);
    const emissions: (RawLineRange | null)[] = [];
    fixture.componentInstance.openTrace.subscribe((r) => emissions.push(r));
    const btn = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="conversation-open-trace"]'
    );
    btn?.click();
    expect(emissions).toEqual([null]);
  });

  it('emits openVerboseDebug from the header button', async () => {
    const fixture = await makeFixture([userMessage()]);
    let opened = 0;
    fixture.componentInstance.openVerboseDebug.subscribe(() => (opened += 1));
    const btn = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="conversation-open-verbose-debug"]'
    );
    btn?.click();
    expect(opened).toBe(1);
  });

  it('skips workbench.* events in slice 1 without throwing', async () => {
    const events: ConversationEvent[] = [
      userMessage(),
      {
        id: 'evt-99',
        kind: 'workbench.summary',
        timestamp: '2026-05-05T12:00:30.000Z',
        rawRange: range(20, 30),
        headline: '4 reads · 1 edit · tests passing',
        aggregate: {},
      },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-feed"]')?.children.length).toBe(1);
  });

  it('coalesces consecutive same-actor agent messages into one bubble with N items', async () => {
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'On branch main'),
      agentMsg('a2', 2, 'Bash completed with no output'),
      agentMsg('a3', 4, '4b02f9c fix(board): replace single MANUAL pill'),
      agentMsg('a4', 6, '[main 579ba96] docs(orchestrator-steering): document STEER reply contract'),
      agentMsg('a5', 8, '579ba96 docs(orchestrator-steering): document STEER reply contract'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const agentBubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(agentBubbles.length).toBe(1);
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(5);
    expect(el.querySelector('[data-testid="conversation-message-count"]')?.textContent).toContain('5 events');
  });

  it('starts a new agent bubble after a user message breaks the run', async () => {
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'first line'),
      agentMsg('a2', 2, 'second line'),
      userMessage(),
      agentMsg('a3', 30, 'after user'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const agentBubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(agentBubbles.length).toBe(2);
    expect(el.querySelectorAll('[data-testid="conversation-message-message.user"]').length).toBe(1);
  });

  it('keeps same-actor messages in one bubble even when their gap exceeds 60s', async () => {
    // Operator request: drop the legacy time-gap rule; same actor stays
    // glued unless USER breaks the run. Two messages 6 minutes apart still
    // fold into a single bubble.
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'first'),
      agentMsg('a2', 360, 'second'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const agentBubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(agentBubbles.length).toBe(1);
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(2);
  });

  it('filters runMarker start events but keeps non-start markers visible', async () => {
    const events: ConversationEvent[] = [
      runMarkerStart('r1', 'c705779a-a6bc-43ac-bada-358ea7e11a28', 0),
      agentMsg('a1', 1, 'On branch main'),
      agentMsg('a2', 2, 'Bash completed'),
      {
        id: 'r2',
        kind: 'runMarker',
        timestamp: new Date(Date.UTC(2026, 4, 5, 12, 0, 10)).toISOString(),
        marker: 'complete',
        rawRange: range(11, 11),
      },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const markerRows = el.querySelectorAll('[data-testid="conversation-run-marker"]');
    expect(markerRows.length).toBe(1);
    expect(markerRows[0].getAttribute('data-marker')).toBe('complete');
  });

  it('attaches the session id from a preceding runMarker to the next agent group as a dezent chip', async () => {
    const sessionId = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
    const events: ConversationEvent[] = [
      runMarkerStart('r1', sessionId, 0),
      agentMsg('a1', 1, 'On branch main'),
      agentMsg('a2', 2, 'Bash completed'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const sessionChip = el.querySelector('[data-testid="conversation-message-session"]');
    expect(sessionChip).toBeTruthy();
    expect(sessionChip?.textContent?.trim()).toContain('c705779a');
    expect(sessionChip?.textContent?.trim()).not.toContain('358ea7e11a28');
    // The bubble carries the full session id as a data attribute so the
    // E2E spec can assert on it without parsing the truncated chip.
    const bubble = el.querySelector('[data-testid="conversation-message-message.taskAgent"]');
    expect(bubble?.getAttribute('data-session-id')).toBe(sessionId);
  });

  it('lifts Session init lines out of the visible flow into sidecar meta', async () => {
    const sessionId = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
    const events: ConversationEvent[] = [
      agentMsg('a1', 0, `● Session init ${sessionId}`),
      agentMsg('a2', 1, 'On branch main'),
      agentMsg('a3', 2, 'Bash completed'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    // Exactly one bubble; the Session init line is NOT one of its items.
    const agentBubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(agentBubbles.length).toBe(1);
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(2);
    for (const item of Array.from(items)) {
      expect(item.textContent).not.toContain('Session init');
    }
    // The session id rides along on the bubble for tooltip / chip rendering.
    expect(agentBubbles[0].getAttribute('data-session-id')).toBe(sessionId);
  });

  it('lifts Rate limit telemetry lines out of the visible flow into sidecar meta', async () => {
    const events: ConversationEvent[] = [
      agentMsg('a1', 0, '● Rate limit · five-hour · allowed · reset in 4.4 h  [window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]'),
      agentMsg('a2', 1, 'On branch main'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const bubble = el.querySelector('[data-testid="conversation-message-message.taskAgent"]');
    expect(bubble).toBeTruthy();
    expect(bubble?.getAttribute('data-has-rate-limit')).toBe('true');
    // No item body contains the raw "Rate limit" string.
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(1);
    expect(items[0].textContent).not.toContain('Rate limit');
    // The dezent dot is the only on-row indicator.
    expect(el.querySelector('[data-testid="conversation-message-rate-limit"]')).toBeTruthy();
  });

  it('strips the "Session task_notification <id>" prefix so the payload is the body', async () => {
    const sessionId = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
    const events: ConversationEvent[] = [
      agentMsg('a1', 0, `● Session task_notification ${sessionId} total 340`),
      agentMsg('a2', 1, `● Session task_notification ${sessionId} session-events.jsonl`),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('total 340');
    expect(items[0].textContent).not.toContain('Session task_notification');
    expect(items[0].textContent).not.toContain(sessionId);
    expect(items[1].textContent).toContain('session-events.jsonl');
  });

  it('skips empty-payload task_started lines but still captures their session id', async () => {
    const sessionId = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
    const events: ConversationEvent[] = [
      agentMsg('a1', 0, `● Session task_started ${sessionId}`),
      agentMsg('a2', 1, 'real payload here'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(1);
    expect(items[0].textContent).toContain('real payload here');
    const bubble = el.querySelector('[data-testid="conversation-message-message.taskAgent"]');
    expect(bubble?.getAttribute('data-session-id')).toBe(sessionId);
  });

  it('limits a long burst to the first 5 items and offers "show N more"', async () => {
    const events: ConversationEvent[] = Array.from({ length: 12 }, (_, i) =>
      agentMsg(`a${i}`, i, `payload ${i}`)
    );
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(5);
    const showMore = el.querySelector<HTMLButtonElement>(
      '[data-testid="conversation-message-show-more"]'
    );
    expect(showMore).toBeTruthy();
    expect(showMore?.textContent).toContain('7');
    // Click expands all 12.
    showMore?.click();
    fixture.detectChanges();
    const expanded = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(expanded.length).toBe(12);
  });

  it('clamps long item bodies and exposes a per-item expand toggle', async () => {
    const longBody = 'lorem ipsum '.repeat(40); // > 180 chars
    const events: ConversationEvent[] = [agentMsg('a1', 0, longBody)];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const item = el.querySelector('[data-testid="conversation-message-item"]');
    expect(item?.classList.contains('msg__item--clampable')).toBe(true);
    const toggle = el.querySelector<HTMLButtonElement>(
      '[data-testid="conversation-message-item-expand"]'
    );
    expect(toggle).toBeTruthy();
    toggle?.click();
    fixture.detectChanges();
    const itemAfter = el.querySelector('[data-testid="conversation-message-item"]');
    expect(itemAfter?.classList.contains('msg__item--expanded')).toBe(true);
  });

  it('does not clamp short single-line items', async () => {
    const events: ConversationEvent[] = [agentMsg('a1', 0, 'short')];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const item = el.querySelector('[data-testid="conversation-message-item"]');
    expect(item?.classList.contains('msg__item--clampable')).toBe(false);
    expect(
      el.querySelector('[data-testid="conversation-message-item-expand"]')
    ).toBeFalsy();
  });

  function deferFeedback(): FeedbackQueuedEvent {
    return {
      id: 'fq-1',
      kind: 'feedback.queued',
      timestamp: '2026-05-05T14:02:00.000Z',
      rawRange: range(40, 41),
      mode: 'defer',
      parentLane: '6-completed',
      label: "I'll get to this when there's bandwidth",
      followUpJobId: 'add-dark-theme-screenshots',
    };
  }

  it('renders a compact feedback.queued marker for a deferred comment', async () => {
    const fixture = await makeFixture([userMessage(), deferFeedback()]);
    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector('[data-testid="conversation-feedback-queued"]');
    expect(row).toBeTruthy();
    expect(row?.getAttribute('data-mode')).toBe('defer');
    expect(row?.getAttribute('data-parent-lane')).toBe('6-completed');
    expect(row?.textContent).toContain('deferred');
    expect(row?.textContent).toContain("bandwidth");
    // Defer carries an "open in queue" affordance to the follow-up task.
    expect(
      el.querySelector('[data-testid="conversation-feedback-open-followup"]')
    ).toBeTruthy();
  });

  it('renders a queued follow-up status band immediately when supplied by the host', async () => {
    const fixture = await makeFixtureWithState([userMessage()], { queuedFollowUp: true });
    const el: HTMLElement = fixture.nativeElement;
    const status = el.querySelector('[data-testid="conversation-status-queued"]');
    expect(status).toBeTruthy();
    expect(status?.textContent).toContain('Queued');
    expect(status?.textContent).toContain('next pickup');
  });

  it('renders the working status band ahead of queued state while the run is active', async () => {
    const fixture = await makeFixtureWithState([userMessage()], {
      isRunning: true,
      queuedFollowUp: true,
    });
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-status-working"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="conversation-status-queued"]')).toBeFalsy();
  });

  it('emits openFollowUp with the follow-up job id when the queue link is clicked', async () => {
    const fixture = await makeFixture([deferFeedback()]);
    const emitted: string[] = [];
    fixture.componentInstance.openFollowUp.subscribe((id) => emitted.push(id));
    const btn = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="conversation-feedback-open-followup"]'
    );
    btn?.click();
    expect(emitted).toEqual(['add-dark-theme-screenshots']);
  });

  it('shows the actor header once across an agent → tool burst → agent thread', async () => {
    // Role continuity: a tool burst between two agent turns preserves the
    // role, so the second group suppresses its repeated header and the run
    // reads as one continuous block.
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'first'),
      toolBurst(),
      agentMsg('a2', 2, 'second'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const groups = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(groups.length).toBe(2);
    expect(groups[0].getAttribute('data-show-header')).toBe('true');
    expect(groups[1].getAttribute('data-show-header')).toBe('false');
    // The suppressed group has no actor header element at all.
    expect(groups[1].querySelector('[data-testid="conversation-message-head"]')).toBeFalsy();
  });

  it('re-announces the actor after a non-tool event resets the role', async () => {
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'first'),
      {
        id: 'r2',
        kind: 'runMarker',
        timestamp: new Date(Date.UTC(2026, 4, 5, 12, 0, 10)).toISOString(),
        marker: 'complete',
        rawRange: range(11, 11),
      },
      agentMsg('a2', 2, 'second'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const groups = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(groups.length).toBe(2);
    expect(groups[1].getAttribute('data-show-header')).toBe('true');
  });

  it('hides tool-burst rows when the Tools toggle is switched off', async () => {
    const fixture = await makeFixture([agentMsg('a1', 1, 'x'), toolBurst()]);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-tool-burst"]')).toBeTruthy();
    const toggle = el.querySelector<HTMLButtonElement>(
      '[data-testid="conversation-show-tools"]'
    );
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');
    toggle?.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="conversation-tool-burst"]')).toBeFalsy();
    expect(toggle?.getAttribute('aria-pressed')).toBe('false');
  });

  it('renders the Session init / Rate limit block as a meta-card row', async () => {
    const sessionId = 'c705779a-a6bc-43ac-bada-358ea7e11a28';
    const events: ConversationEvent[] = [
      agentMsg('a1', 0, `● Session init ${sessionId}`),
      agentMsg(
        'a2',
        1,
        '● Rate limit · five-hour · allowed · reset in 4.4 h  [window=five_hour status=allowed resetsAt=1777393800]'
      ),
      agentMsg('a3', 2, 'On branch main'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-session-meta"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="conversation-session-card"]')).toBeTruthy();
    expect(
      el.querySelector('[data-testid="conversation-session-card-id"]')?.textContent
    ).toContain('c705779a');
    expect(
      el.querySelector('[data-testid="conversation-session-card-ratelimit"]')?.textContent
    ).toContain('5h');
    // The init lines still do not render as message items.
    const items = el.querySelectorAll('[data-testid="conversation-message-item"]');
    expect(items.length).toBe(1);
    expect(items[0].textContent).toContain('On branch main');
  });

  it('renders an answered Ask marker without a queue link', async () => {
    const ask: FeedbackQueuedEvent = {
      id: 'fq-2',
      kind: 'feedback.queued',
      timestamp: '2026-05-05T14:05:00.000Z',
      rawRange: range(42, 43),
      mode: 'ask',
      parentLane: '7-archive',
      label: 'answered inline · no code changes',
      answered: true,
    };
    const fixture = await makeFixture([ask]);
    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector('[data-testid="conversation-feedback-queued"]');
    expect(row?.getAttribute('data-mode')).toBe('ask');
    expect(row?.textContent).toContain('asked');
    expect(
      el.querySelector('[data-testid="conversation-feedback-open-followup"]')
    ).toBeFalsy();
    // Trace stays reachable on every marker.
    expect(
      el.querySelector('[data-testid="conversation-feedback-open-trace"]')
    ).toBeTruthy();
  });

  it('renders the generating model subtly next to the timestamp', async () => {
    const events: ConversationEvent[] = [
      { ...agentMsg('a1', 1, 'building the badge'), model: 'claude-opus-4-8' },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const head = el.querySelector('[data-testid="conversation-message-head"]');
    const badge = el.querySelector('[data-testid="conversation-message-model"]');
    expect(badge).toBeTruthy();
    expect(badge?.textContent?.trim()).toBe('claude-opus-4-8');
    // The badge sits inside the same header as the timestamp.
    expect(head?.contains(badge as Node)).toBe(true);
  });

  it('omits the model badge when the output has no attributable model', async () => {
    const fixture = await makeFixture([agentMsg('a1', 1, 'no model here')]);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="conversation-message-model"]')).toBeFalsy();
  });

  it('breaks the bubble on a mid-task model switch so each bubble names one model', async () => {
    // Same actor, different model: the switch must close the first bubble so a
    // recovery run on another model renders its own model next to its time.
    const events: ConversationEvent[] = [
      { ...agentMsg('a1', 1, 'first run reply'), model: 'gpt-5-codex' },
      { ...agentMsg('a2', 2, 'recovery run reply'), model: 'claude-opus-4-7' },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const bubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(bubbles.length).toBe(2);
    const badges = el.querySelectorAll('[data-testid="conversation-message-model"]');
    expect(Array.from(badges).map((b) => b.textContent?.trim())).toEqual([
      'gpt-5-codex',
      'claude-opus-4-7',
    ]);
  });

  it('keeps same-actor same-model messages in one bubble with a single model badge', async () => {
    const events: ConversationEvent[] = [
      { ...agentMsg('a1', 1, 'first'), model: 'claude-opus-4-8' },
      { ...agentMsg('a2', 2, 'second'), model: 'claude-opus-4-8' },
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const bubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(bubbles.length).toBe(1);
    const badges = el.querySelectorAll('[data-testid="conversation-message-model"]');
    expect(badges.length).toBe(1);
    expect(badges[0].textContent?.trim()).toBe('claude-opus-4-8');
  });

  it('surfaces the full date behind the message timestamp on hover (criterion: dated times)', async () => {
    // The visible <time> shows only the clock; the date lives in the tooltip
    // so an operator can tell which calendar day a turn happened.
    const fixture = await makeFixture([agentMsg('a1', 1, 'dated turn')]);
    const el: HTMLElement = fixture.nativeElement;
    const time = el.querySelector('[data-testid="conversation-message-time"]');
    expect(time).toBeTruthy();
    // The visible clock label omits the calendar date.
    expect(time?.textContent ?? '').not.toMatch(/2026/);
    // The tooltip the template binds (groupTimeTooltip) carries the full date.
    const inst = fixture.componentInstance;
    const group = inst.rows().find((r) => r.kind === 'messageGroup');
    expect(group).toBeTruthy();
    const dated = inst.groupTimeTooltip(group as never);
    expect(dated).toMatch(/2026/);
    expect(dated).toMatch(/May/);
  });

  it('formatDateTime returns a calendar+clock string and formatTime stays clock-only', async () => {
    const fixture = await makeFixture([agentMsg('a1', 1, 'x')]);
    const inst = fixture.componentInstance;
    const iso = '2026-05-05T12:00:01.000Z';
    expect(inst.formatDateTime(iso)).toMatch(/2026/);
    expect(inst.formatDateTime(iso)).toMatch(/May/);
    expect(inst.formatTime(iso)).not.toMatch(/2026/);
    // Empty / invalid inputs degrade gracefully.
    expect(inst.formatDateTime('')).toBe('');
    expect(inst.formatDateTime('not-a-date')).toBe('');
  });

  function decision(decisionType: string, severity?: 'info' | 'warn' | 'error') {
    return {
      id: `dec-${decisionType}`,
      kind: 'decision.orchestrator' as const,
      timestamp: '2026-05-05T12:01:00.000Z',
      rawRange: range(50, 55),
      decisionType,
      reason: `orchestrator chose ${decisionType}`,
      action: 'continue',
      ...(severity ? { severity } : {}),
    };
  }

  it('renders an orchestrator decision with a humanised type label and accent rail (criterion: upgraded decisions)', async () => {
    const fixture = await makeFixture([decision('auto-review')]);
    const el: HTMLElement = fixture.nativeElement;
    const row = el.querySelector('[data-testid="conversation-decision-orchestrator"]');
    expect(row).toBeTruthy();
    expect(row?.getAttribute('data-decision-type')).toBe('auto-review');
    // The raw kebab kind never leaks into the visible label.
    const type = el.querySelector('[data-testid="conversation-decision-type"]');
    expect(type?.textContent?.trim()).toBe('Auto-Review');
    expect(type?.textContent).not.toContain('auto-review');
    // The decision keeps its own prominent accent rail, set apart from a
    // plain agent bubble.
    expect(row?.querySelector('.decision__rail')).toBeTruthy();
    expect(el.querySelector('[data-testid="conversation-message-message.taskAgent"]')).toBeFalsy();
  });

  it('maps every known decision kind to a presentable label and title-cases unknown kinds', async () => {
    const fixture = await makeFixture([decision('auto-review')]);
    const inst = fixture.componentInstance;
    expect(inst.decisionTypeLabel('auto-review')).toBe('Auto-Review');
    expect(inst.decisionTypeLabel('reissue')).toBe('Reissue');
    expect(inst.decisionTypeLabel('reissue-open-items')).toBe('Reissue · Open items');
    expect(inst.decisionTypeLabel('accept')).toBe('Accept');
    expect(inst.decisionTypeLabel('escalate')).toBe('Escalate');
    expect(inst.decisionTypeLabel('worktree-containment')).toBe('Worktree containment');
    expect(inst.decisionTypeLabel('environment-blocker')).toBe('Environment blocker');
    // Unknown kind falls back to a title-cased version of the kebab/snake form.
    expect(inst.decisionTypeLabel('some_new-kind')).toBe('Some New Kind');
    // Null / undefined degrade to a neutral label.
    expect(inst.decisionTypeLabel(undefined)).toBe('Decision');
    expect(inst.decisionTypeLabel(null)).toBe('Decision');
  });

  it('carries the decision severity onto the row so the accent can shift (warn/error/accept)', async () => {
    const fixture = await makeFixture([
      decision('escalate', 'error'),
      decision('accept'),
    ]);
    const el: HTMLElement = fixture.nativeElement;
    const rows = el.querySelectorAll('[data-testid="conversation-decision-orchestrator"]');
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-severity')).toBe('error');
    expect(rows[0].getAttribute('data-decision-type')).toBe('escalate');
    // No explicit severity → default 'info'; the type still drives the accent.
    expect(rows[1].getAttribute('data-severity')).toBe('info');
    expect(rows[1].getAttribute('data-decision-type')).toBe('accept');
  });
});
