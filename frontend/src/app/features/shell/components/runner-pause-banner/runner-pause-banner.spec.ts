import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { RunnerPauseBannerComponent } from './runner-pause-banner';

describe('RunnerPauseBannerComponent', () => {
  it('names the infra breaker reason and automatic recovery', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerPauseBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunnerPauseBannerComponent);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/status').flush({
      projects: {
        'Agent Studio': {
          projectName: 'Agent Studio',
          mode: 'manual',
          activeJobId: null,
          activeExecution: null,
          queuedJobIds: [],
          modeReason: 'pickup paused: infra breaker, 3 failures cliType=claude at 2026-08-24T00:20:00Z',
          modeChangedAt: '2026-08-24T00:20:00Z',
          modeSource: 'circuit-breaker',
          breakerState: 'cooldown',
          breakerCooldownUntil: '2026-08-24T00:30:00Z',
        },
      },
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-testid="runner-pause-banner"]') as HTMLElement;
    expect(banner.textContent).toContain('Pickup paused: infra breaker in Agent Studio');
    expect(banner.textContent).toContain('3 failures cliType=claude');
    expect(banner.textContent).toContain('Recovery is automatic');
    fixture.destroy();
    http.verify();
  });

  it('does not present an operator manual mode as an infra pause', async () => {
    await TestBed.configureTestingModule({
      imports: [RunnerPauseBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RunnerPauseBannerComponent);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/runner/status').flush({
      projects: {
        demo: {
          projectName: 'demo', mode: 'manual', activeJobId: null,
          activeExecution: null, queuedJobIds: [], modeReason: 'api-toggle', modeSource: 'user',
        },
      },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="runner-pause-banner"]')).toBeNull();
    fixture.destroy();
    TestBed.inject(HttpTestingController).verify();
  });
});
