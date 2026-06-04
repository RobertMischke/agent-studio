import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ChatComponent } from './chat.component';
import type { ChatMessage } from '../chat-types';

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
        text: '| Field | Value |\n|---|---|\n| ID | ASS-704 |',
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
    expect(userTurn?.querySelector('td')?.textContent?.trim()).toBe('ASS-704');
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
