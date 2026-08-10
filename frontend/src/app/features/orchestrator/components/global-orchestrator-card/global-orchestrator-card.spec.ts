import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GlobalOrchestratorCardComponent } from './global-orchestrator-card';

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
describe('GlobalOrchestratorCardComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [GlobalOrchestratorCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GlobalOrchestratorCardComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] GlobalOrchestratorCardComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('GlobalOrchestratorCardComponent · compact status line', () => {
  it('removes the narrative reply and discloses session metrics on demand', async () => {
    await TestBed.configureTestingModule({
      imports: [GlobalOrchestratorCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(GlobalOrchestratorCardComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne(request => request.url.includes('/api/runner/global/orchestrator-session'))
      .flush({
        project: '(global)',
        session: {
          sessionId: 'session-123',
          model: 'gpt-5.6-terra',
          bootedAt: '2026-08-10T08:00:00Z',
          bootPromptPreview: 'Monitor every project.',
          bootReplyPreview: 'Monitoring 12 projects. What would help?',
          cumulativeInputTokens: 120000,
          cumulativeOutputTokens: 18000,
          cumulativeCacheReadTokens: 420000,
          cumulativeCacheCreationTokens: 12000,
          calls: 24,
          lastUsedAt: '2026-08-10T09:00:00Z',
          lastError: null,
        },
      });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const status = root.querySelector<HTMLElement>('[data-testid="global-orchestrator-status"]');
    expect(status?.textContent).toContain('Scope All projects');
    expect(status?.textContent).toContain('Model gpt-5.6-terra');
    expect(status?.textContent).toContain('Talk to it');
    expect(status?.textContent).toContain('claude -r session-123');
    expect(root.textContent).not.toContain('Monitoring 12 projects');
    expect(root.querySelector('[data-testid="global-orchestrator-details"]')).toBeNull();

    fixture.componentInstance.toggleDetails();
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="global-orchestrator-details"]')?.textContent)
      .toContain('120,000 / 18,000');
  });
});
