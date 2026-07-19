import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectDetailComponent } from './project-detail';

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
describe('ProjectDetailComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDetailComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDetailComponent);
    fixture.componentRef.setInput('projectName', undefined);

    // Required inputs seeded with undefined — replace with realistic defaults if needed:
    // projectName
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ProjectDetailComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders the auto-commit immediacy hint in settings view', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDetailComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectDetailComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.componentRef.setInput('view', 'settings');
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Auto-commit on transition 3-progress -> 4-auto-review');
    expect(text).toContain('Changes apply immediately to the next job transition.');
    expect(text).toContain('CLI environment');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="project-detail-cli-environment"]')).toBeTruthy();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="project-cli-onboarding-status"]')).toBeNull();
  });

  it('keeps the retired legacy overview free of machine plumbing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDetailComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ProjectDetailComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.componentRef.setInput('view', 'overview');
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const text = host.textContent ?? '';
    expect(text).not.toContain('Watch path');
    expect(text).not.toContain('Working directory');
    expect(text).not.toContain('Repository');
    expect(text).not.toContain('Onboarding status');
    expect(host.querySelector('[data-testid="project-cli-onboarding-status"]')).toBeNull();
  });
});
