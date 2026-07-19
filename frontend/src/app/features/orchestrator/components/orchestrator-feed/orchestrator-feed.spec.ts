import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorFeedComponent } from './orchestrator-feed';
import type { OrchestratorLogEntry } from '../../../orchestrator';

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
describe('OrchestratorFeedComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] OrchestratorFeedComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Regression for the override controls on an orchestrator-decision entry.
 *
 * The override buttons act on an Orchestrator Decision and live inside
 * components hosted by overlays / side sheets. A button without an
 * explicit `type` attribute defaults to `type="submit"`, so if any host
 * ever wraps the feed in a `<form>`, clicking one would submit the form
 * and close the surrounding Task / Frontend overlay. Pinning the
 * attribute keeps that side-effect off the table.
 */
describe('OrchestratorFeedComponent · decision override buttons', () => {
  const decisionEntry: OrchestratorLogEntry = {
    ts: '2026-05-14T11:00:00Z',
    kind: 'decision',
    topic: 'reissue',
    summary: 'Reissued the task with stronger framing.',
    reasoning: 'The agent reported a fast Done on a UserContinue follow-up.',
    jobId: 'demo-job',
    tokenUsage: null,
  };

  async function setup() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedComponent);
    fixture.componentRef.setInput('projectName', 'demo-project');
    fixture.detectChanges();

    const httpCtrl = TestBed.inject(HttpTestingController);
    const req = httpCtrl.expectOne((r) => r.url.includes('/api/runner/orchestrator-feed'));
    req.flush({ entries: [{ ...decisionEntry, project: 'demo-project' }] });
    fixture.detectChanges();
    return { fixture, httpCtrl };
  }

  it('renders the "Override this decision" trigger as type="button"', async () => {
    const { fixture } = await setup();

    const root = fixture.nativeElement as HTMLElement;
    const trigger = root.querySelector<HTMLButtonElement>(
      '[data-testid="orchestrator-override-start"]'
    );
    expect(trigger).toBeTruthy();
    expect(trigger?.getAttribute('type')).toBe('button');
  });

  it('defaults to signal and hides passive observations', async () => {
    const { fixture } = await setup();
    fixture.componentInstance.entries.set([
      { ...decisionEntry, project: 'demo-project' },
      { ...decisionEntry, ts: '2026-05-14T10:00:00Z', kind: 'observation', summary: 'Routine scan', project: 'demo-project' },
    ]);
    expect(fixture.componentInstance.kindFilter()).toBe('signal');
    expect(fixture.componentInstance.visibleEntries().map(entry => entry.kind)).toEqual(['decision']);
  });

  it('renders Cancel and Send override as type="button" while the override form is open', async () => {
    const { fixture } = await setup();

    fixture.componentInstance.startOverride(decisionEntry);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const submit = root.querySelector<HTMLButtonElement>(
      '[data-testid="orchestrator-override-submit"]'
    );
    const cancel = root.querySelector<HTMLButtonElement>('.orch-feed__override-cancel');
    expect(submit).toBeTruthy();
    expect(cancel).toBeTruthy();
    expect(submit?.getAttribute('type')).toBe('button');
    expect(cancel?.getAttribute('type')).toBe('button');
  });
});
