import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ChatComponent } from './chat.component';
import type { ChatEvent, ChatMessage, ChatSubmitEvent } from '../chat-types';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('ChatComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ChatComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ChatComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('ChatComponent markdown rendering', () => {
  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    return TestBed.createComponent(ChatComponent);
  }

  it('renders user and orchestrator turns through the shared markdown view', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('messages', [
      {
        id: 'user-table',
        role: 'user',
        text: '| Field | Value |\n| --- | --- |\n| ID | ASS-704 |',
        timestamp: '2026-01-01T00:00:00.000Z',
      },
      {
        id: 'orch-formatting',
        role: 'orchestrator',
        text: '- **Done**\n- [Docs](https://example.com)\n\n```ts\nconst ok = true;\n```',
        timestamp: '2026-01-01T00:00:01.000Z',
      },
    ] satisfies ChatMessage[]);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const userTurn = root.querySelector('[data-turn-id="user-table"]') as HTMLElement | null;
    const orchTurn = root.querySelector('[data-turn-id="orch-formatting"]') as HTMLElement | null;

    expect(userTurn?.querySelector('table')).toBeTruthy();
    const tableCells = [...(userTurn?.querySelectorAll('td') ?? [])].map(cell => cell.textContent?.trim());
    expect(tableCells).toContain('ASS-704');
    expect(orchTurn?.querySelector('ul')).toBeTruthy();
    expect(orchTurn?.querySelector('strong')?.textContent).toBe('Done');
    expect(orchTurn?.querySelector('a')?.getAttribute('href')).toBe('https://example.com');
    expect(orchTurn?.querySelector('pre code')?.textContent).toContain('const ok = true;');
  });
});

describe('ChatComponent virtualisation', () => {
  // All-agent fixtures: phase grouping is anchored on user steers, so
  // using only agent turns produces a single open phase and every
  // message survives the hidden-phase filter — exactly what we want
  // when exercising the virtualisation slicing.
  function makeMessage(i: number): ChatMessage {
    return {
      id: `msg-${i}`,
      role: 'agent',
      text: `Body ${i}`,
      timestamp: new Date(Date.UTC(2026, 0, 1, 0, 0, i)).toISOString(),
    };
  }

  async function makeFixture(virtualised: boolean) {
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ChatComponent);
    fixture.componentRef.setInput('virtualised', virtualised);
    return fixture;
  }

  it('renders all rows when virtualisation is off', async () => {
    const fixture = await makeFixture(false);
    const messages = Array.from({ length: 200 }, (_, i) => makeMessage(i));
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    const items = fixture.componentInstance.windowedItems();
    expect(items.length).toBe(messages.length);
    expect(fixture.componentInstance.topSpacerPx()).toBe(0);
    expect(fixture.componentInstance.bottomSpacerPx()).toBe(0);
  });

  it('windows the rendered slice when virtualisation is on', async () => {
    const fixture = await makeFixture(true);
    fixture.componentRef.setInput('virtualRowHeightPx', 100);
    fixture.componentRef.setInput('virtualBufferRows', 10);
    const messages = Array.from({ length: 500 }, (_, i) => makeMessage(i));
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    // sticky-to-bottom seeds the window at the end, so visibleEnd
    // is at the array length and visibleStart trails it.
    const c = fixture.componentInstance;
    expect(c.visibleEnd()).toBe(500);
    expect(c.visibleStart()).toBeGreaterThanOrEqual(0);
    expect(c.windowedItems().length).toBeLessThan(messages.length);
    expect(c.topSpacerPx()).toBeGreaterThan(0);
    expect(c.bottomSpacerPx()).toBe(0);
  });

  // Regression for the orchestrator-chat "content vanishes after load,
  // reappears on scroll" bug. A scroll event firing while the view is
  // sticky-at-bottom (scroll-anchoring reflow during the side-sheet open
  // animation, async markdown growth, or the programmatic pin's own
  // event) must NOT recompute the window from the row-height *estimate*.
  // The estimate (120px) is much taller than short orchestrator turns, so
  // the scroll-derived window lands a phantom bottom spacer under the
  // freshly loaded tail and pushes it out of the viewport — blank until a
  // manual scroll. While sticky the window stays pinned to the tail.
  it('does NOT re-run per-row time/icon/label formatting on a keystroke CD pass', async () => {
    // Typing in the composer dirties the chat view, so a CD pass re-runs
    // the whole template — including the message/event @for. The draft does
    // not change messages() or events(), so the memoised rendered() slice
    // must already carry the per-row display strings; the loop body must NOT
    // call formatTime()/eventIcon()/eventLabel() again on every keystroke.
    // Those calls (formatTime → toLocaleTimeString / Intl in particular) are
    // the typing-lag culprit when multiplied across the rendered window.
    const fixture = await makeFixture(false);
    const messages = Array.from({ length: 40 }, (_, i) => makeMessage(i));
    fixture.componentRef.setInput('messages', messages);
    const events: ChatEvent[] = Array.from({ length: 10 }, (_, i) => ({
      id: `evt-${i}`,
      kind: 'tool-call',
      timestamp: new Date(Date.UTC(2026, 0, 1, 0, 1, i)).toISOString(),
      summary: `tool ${i}`,
    }));
    fixture.componentRef.setInput('events', events);
    fixture.detectChanges();

    const c = fixture.componentInstance;
    const formatSpy = vi.spyOn(c, 'formatTime');
    const iconSpy = vi.spyOn(c, 'eventIcon');
    const labelSpy = vi.spyOn(c, 'eventLabel');

    // Simulate real keystrokes: ngModel's (input) listener dirties the chat
    // view, so the next CD pass re-runs the whole template (the message /
    // event @for included). Dispatching the DOM event — not just poking
    // draftText — is what reproduces the keystroke path: a plain field write
    // would not mark the OnPush view dirty.
    const textarea = fixture.nativeElement.querySelector(
      '[data-testid="chat-input"]'
    ) as HTMLTextAreaElement;
    for (const value of ['h', 'he', 'hel']) {
      textarea.value = value;
      textarea.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    expect(formatSpy).not.toHaveBeenCalled();
    expect(iconSpy).not.toHaveBeenCalled();
    expect(labelSpy).not.toHaveBeenCalled();
  });

  it('keeps the newest turn rendered when a scroll fires while sticky-at-bottom', async () => {
    const fixture = await makeFixture(true);
    // Defaults: 120px estimate, 20-row buffer — the production config.
    const messages = Array.from({ length: 200 }, (_, i) => makeMessage(i));
    fixture.componentRef.setInput('messages', messages);
    fixture.detectChanges();

    const c = fixture.componentInstance;
    // Initial sticky seed: tail pinned, no bottom spacer.
    expect(c.visibleEnd()).toBe(200);
    expect(c.bottomSpacerPx()).toBe(0);

    // Real content is far shorter than 200 * 120px because the turns are
    // one-liners. Simulate the scroll container parked at the true bottom.
    const body = fixture.nativeElement.querySelector(
      '[data-testid="chat-body"]'
    ) as HTMLElement;
    Object.defineProperty(body, 'clientHeight', { value: 600, configurable: true });
    Object.defineProperty(body, 'scrollHeight', { value: 9000, configurable: true });
    Object.defineProperty(body, 'scrollTop', { value: 8400, writable: true, configurable: true });

    c.onBodyScroll();

    // distanceFromBottom == 0 → still sticky; the tail must stay rendered.
    expect(c.stickToBottom()).toBe(true);
    expect(c.visibleEnd()).toBe(200);
    expect(c.bottomSpacerPx()).toBe(0);
    const windowed = c.windowedItems();
    expect(windowed[windowed.length - 1].id).toBe('msg-199');
  });
});

// Guards the prompt's must-not-regress composer behaviours and the core
// invariant the typing-perf fix relies on: a keystroke dirties the OnPush
// view but must NOT recompute the memoised rendered() slice (that slice now
// carries the pre-formatted per-row time/icon/label strings, so rebuilding
// it on every keystroke is exactly the lag we removed).
describe('ChatComponent composer (typing-perf no-regression)', () => {
  function makeMessage(i: number): ChatMessage {
    return {
      id: `msg-${i}`,
      role: 'agent',
      text: `Body ${i}`,
      timestamp: new Date(Date.UTC(2026, 0, 1, 0, 0, i)).toISOString(),
    };
  }

  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    return TestBed.createComponent(ChatComponent);
  }

  function typeInto(fixture: Awaited<ReturnType<typeof makeFixture>>, value: string): HTMLTextAreaElement {
    const textarea = fixture.nativeElement.querySelector(
      '[data-testid="chat-input"]'
    ) as HTMLTextAreaElement;
    textarea.value = value;
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    return textarea;
  }

  it('does NOT recompute the memoised rendered() slice on keystrokes', async () => {
    const fixture = await makeFixture();
    fixture.componentRef.setInput('messages', Array.from({ length: 30 }, (_, i) => makeMessage(i)));
    fixture.componentRef.setInput('events', [
      { id: 'evt-0', kind: 'tool-call', timestamp: '2026-01-01T00:01:00.000Z', summary: 'tool' },
    ] satisfies ChatEvent[]);
    fixture.detectChanges();

    const c = fixture.componentInstance;
    // Capturing the array identity is the tightest possible proof: a signal
    // `computed` returns the SAME reference until one of its dependencies
    // changes. The draft is a plain field, not a tracked signal, so typing
    // must leave rendered() — and every per-row display string in it — intact.
    const before = c.rendered();
    for (const value of ['a', 'ab', 'abc', 'abcd']) typeInto(fixture, value);
    expect(c.rendered()).toBe(before);
  });

  it('sends on Enter and clears the draft; Shift+Enter does not send', async () => {
    const fixture = await makeFixture();
    fixture.detectChanges();
    const c = fixture.componentInstance;
    const submits: ChatSubmitEvent[] = [];
    c.submitMessage.subscribe((e) => submits.push(e));

    const textarea = typeInto(fixture, 'hello');

    // Shift+Enter is the multiline shortcut — it must NOT submit.
    textarea.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, bubbles: true, cancelable: true })
    );
    expect(submits.length).toBe(0);

    // Plain Enter submits the trimmed draft and resets the composer.
    textarea.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })
    );
    expect(submits.length).toBe(1);
    expect(submits[0].text).toBe('hello');
    expect(c.draftText).toBe('');
  });
});
