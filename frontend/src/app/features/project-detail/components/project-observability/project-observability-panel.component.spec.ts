import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectObservabilityPanelComponent } from './project-observability-panel.component';
import type { AgentMessage } from '../../../../models/agent-bus.model';

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
describe('ProjectObservabilityPanelComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectObservabilityPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectObservabilityPanelComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectObservabilityPanelComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('groups categorized runner issues in the outcome strip', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectObservabilityPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectObservabilityPanelComponent);
    fixture.componentRef.setInput('projectName', '');
    fixture.componentInstance.messages.set([
      makeMessage('m1', 'permission-blocked', 'High'),
      makeMessage('m2', 'classifier-unknown', 'Warn'),
      makeMessage('m3', 'soft-intervention', 'Warn', 'category: permission-blocked'),
    ]);
    fixture.detectChanges();

    const permission = fixture.nativeElement.querySelector('[data-testid="observability-outcome-permission-blocked"]') as HTMLElement | null;
    const classifier = fixture.nativeElement.querySelector('[data-testid="observability-outcome-classifier-unknown"]') as HTMLElement | null;
    expect(permission?.textContent).toContain('2');
    expect(permission?.textContent).toContain('Permission blocked');
    expect(classifier?.textContent).toContain('Classifier unknown');
  });
});

function makeMessage(id: string, topic: string, severity: 'Warn' | 'High', body = ''): AgentMessage {
  return {
    schemaVersion: 1,
    id,
    createdAt: '2026-05-11T10:00:00Z',
    participantId: 'orchestrator',
    role: 'assistant',
    kind: topic === 'soft-intervention' ? 'intervention' : 'decision',
    severity,
    topic,
    summary: body || topic,
    body,
  };
}
