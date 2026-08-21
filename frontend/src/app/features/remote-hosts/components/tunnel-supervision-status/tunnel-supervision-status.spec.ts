import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { TunnelSupervisionStatusComponent } from './tunnel-supervision-status';
import type { TunnelSupervisionResponse } from '../../models/tunnel-supervision.model';

describe('TunnelSupervisionStatusComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [TunnelSupervisionStatusComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    return {
      fixture: TestBed.createComponent(TunnelSupervisionStatusComponent),
      httpMock: TestBed.inject(HttpTestingController),
    };
  }

  it('stays hidden when the deployment has never run the guided registration', () => {
    const { fixture, httpMock } = setup();
    fixture.detectChanges();
    httpMock.expectOne('/api/system/tunnel-supervision').flush({
      overall: 'not-configured',
      snapshot: null,
    } satisfies TunnelSupervisionResponse);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="tunnel-supervision-status"]')).toBeNull();
    httpMock.verify();
  });

  it('shows registered / running / last-heal facts for a configured host', () => {
    const { fixture, httpMock } = setup();
    fixture.detectChanges();
    httpMock.expectOne('/api/system/tunnel-supervision').flush({
      overall: 'healthy',
      snapshot: {
        schemaVersion: 1,
        generatedAt: '2026-08-21T12:00:00Z',
        keeper: {
          taskName: 'AgentRunner-TunnelKeeper', registered: true, state: 'Running',
          lastStatus: 'healthy', lastObservedAt: '2026-08-21T11:59:00Z', lastMessage: null,
        },
        watchdog: {
          taskName: 'AgentRunner-TunnelWatchdog', registered: true, state: 'Running',
          lastProbeAt: '2026-08-21T11:59:30Z', lastProbeResult: 'ok',
          lastHealAt: '2026-08-21T10:30:00Z', lastHealResult: 'succeeded',
          consecutiveProbeFailures: 0,
        },
      },
    } satisfies TunnelSupervisionResponse);
    fixture.detectChanges();

    const section: HTMLElement = fixture.nativeElement.querySelector('[data-testid="tunnel-supervision-status"]');
    expect(section).not.toBeNull();
    expect(section.getAttribute('data-tone')).toBe('ok');
    expect(section.textContent).toContain('healthy');
    expect(section.textContent).toContain('registered: yes');
    expect(section.textContent).toContain('succeeded');
    httpMock.verify();
  });

  it('marks an unregistered watchdog as attention with the error tone', () => {
    const { fixture, httpMock } = setup();
    fixture.detectChanges();
    httpMock.expectOne('/api/system/tunnel-supervision').flush({
      overall: 'attention',
      snapshot: {
        schemaVersion: 1,
        generatedAt: '2026-08-21T12:00:00Z',
        keeper: {
          taskName: 'AgentRunner-TunnelKeeper', registered: true, state: 'Running',
          lastStatus: 'healthy', lastObservedAt: '2026-08-21T11:59:00Z', lastMessage: null,
        },
        watchdog: {
          taskName: 'AgentRunner-TunnelWatchdog', registered: false, state: null,
          lastProbeAt: null, lastProbeResult: null, lastHealAt: null, lastHealResult: null,
          consecutiveProbeFailures: null,
        },
      },
    } satisfies TunnelSupervisionResponse);
    fixture.detectChanges();

    const section: HTMLElement = fixture.nativeElement.querySelector('[data-testid="tunnel-supervision-status"]');
    expect(section.getAttribute('data-tone')).toBe('error');
    const watchdog: HTMLElement = fixture.nativeElement.querySelector('[data-testid="tunnel-supervision-watchdog"]');
    expect(watchdog.textContent).toContain('registered: no');
    httpMock.verify();
  });
});
