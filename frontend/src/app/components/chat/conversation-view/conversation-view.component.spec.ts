import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ConversationViewComponent } from './conversation-view.component';
import type {
  ConversationEvent,
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

  it('starts a new agent bubble when the gap between same-actor messages exceeds 60s', async () => {
    const events: ConversationEvent[] = [
      agentMsg('a1', 1, 'first'),
      // 90s later → past the coalesce threshold
      agentMsg('a2', 91, 'second'),
    ];
    const fixture = await makeFixture(events);
    const el: HTMLElement = fixture.nativeElement;
    const agentBubbles = el.querySelectorAll('[data-testid="conversation-message-message.taskAgent"]');
    expect(agentBubbles.length).toBe(2);
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
    // start row is filtered out
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
    // Short-form id, not the full uuid
    expect(sessionChip?.textContent?.trim()).toContain('c705779a');
    expect(sessionChip?.textContent?.trim()).not.toContain('358ea7e11a28');
  });
});
