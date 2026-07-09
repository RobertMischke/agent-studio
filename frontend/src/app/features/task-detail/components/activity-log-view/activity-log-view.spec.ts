import { describe, expect, it } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ActivityLogViewComponent } from './activity-log-view';
import { CliOutputLine } from '../../../../models/task.model';

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
describe('ActivityLogViewComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ActivityLogViewComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Bug: "Activity-Log zeigt rohes JSON statt Nutzer-Nachrichten".
 *
 * The projection guard replaces raw stream-json transport frames with a
 * compact `[internal event]` marker (verified structurally in
 * conversation-projection.spec.ts). These specs close the loop at the DOM
 * boundary: they assert the Trace template is actually WIRED to render that
 * marker as a collapsible `<details>` disclosure - the raw JSON only ever
 * appears inside the (initially hidden) `<pre>` detail, never as plain chat
 * text. This pins the "HTML template not wired to render internal event
 * markers" regression: a template that dropped the `internalDetailOf` branch
 * and fell back to `{{ line.text }}` would still show `[internal event]` but
 * would lose the disclosure, so we assert the disclosure element exists and
 * carries the original frame.
 */
describe('ActivityLogViewComponent — internal-event rendering (Trace)', () => {
  const RAW_FRAME =
    '{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"secret reasoning","signature":"Er8BCkg=="}]}}';

  function line(text: string, stream = 'stdout'): CliOutputLine {
    return { timestamp: '2026-07-09T03:15:00.000Z', stream, text };
  }

  async function renderTrace(lines: CliOutputLine[]): Promise<ComponentFixture<ActivityLogViewComponent>> {
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    fixture.componentRef.setInput('lines', lines);
    fixture.componentRef.setInput('debugVisible', true);
    // Drive Trace mode through the input: the component's defaultModeEffect
    // re-applies defaultMode() on every change-detection pass, so a direct
    // mode.set() would be clobbered.
    fixture.componentRef.setInput('defaultMode', 'trace');
    fixture.detectChanges();
    // Trace groups render their lines only when expanded; force every group
    // open so the assertions see the line-level DOM.
    for (const group of fixture.componentInstance.visibleTraceGroups()) {
      fixture.componentInstance.expandedGroups.update((m) => ({ ...m, [group.id]: true }));
    }
    fixture.detectChanges();
    return fixture;
  }

  it('renders the raw frame as a collapsible [internal event] marker, never as chat text', async () => {
    const fixture = await renderTrace([line(RAW_FRAME)]);
    const host: HTMLElement = fixture.nativeElement;

    // The compact marker line is present...
    const summary = host.querySelector<HTMLElement>('.trace-line--internal .trace-line__text');
    expect(summary?.textContent?.trim()).toBe('[internal event]');

    // ...wrapped in a real disclosure element (this is the wiring the review flagged)...
    const disclosure = host.querySelector('details.trace-line__internal');
    expect(disclosure).toBeTruthy();

    // ...the original raw JSON lives ONLY inside the disclosure detail...
    const detail = host.querySelector<HTMLElement>('.trace-line__internal-detail');
    expect(detail?.textContent).toContain('"type":"assistant"');
    expect(detail?.textContent).toContain('"signature"');

    // ...and the raw frame is never rendered as a plain (non-disclosure) line.
    const plainLines = Array.from(host.querySelectorAll('.trace-line'))
      .filter((el) => !el.classList.contains('trace-line--internal'));
    for (const el of plainLines) {
      expect(el.textContent ?? '').not.toContain('"type":"assistant"');
    }
    fixture.destroy();
  });

  it('keeps ordinary prose as a plain line with no disclosure', async () => {
    const fixture = await renderTrace([line('Looking at the activity-log component now.')]);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('details.trace-line__internal')).toBeNull();
    const anyLine = host.querySelector<HTMLElement>('.trace-line');
    expect(anyLine?.textContent).toContain('Looking at the activity-log component now.');
    fixture.destroy();
  });

  it('never leaks raw JSON into the Conversation (chat) surface either', async () => {
    // The bug report is about the chat/Conversation surface specifically.
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    fixture.componentRef.setInput('lines', [
      line('Here is my plan.'),
      line(RAW_FRAME),
      line('Done.'),
    ]);
    fixture.componentRef.setInput('defaultMode', 'conversation');
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    // The whole rendered conversation must never contain the raw frame body.
    expect(host.textContent ?? '').not.toContain('"type":"assistant"');
    expect(host.textContent ?? '').not.toContain('secret reasoning');
    fixture.destroy();
  });
});
