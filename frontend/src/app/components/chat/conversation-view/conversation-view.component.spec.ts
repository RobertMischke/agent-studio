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
});
