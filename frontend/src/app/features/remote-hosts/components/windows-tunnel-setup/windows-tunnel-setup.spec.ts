import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { WindowsTunnelStatus } from '../../models/windows-tunnel.model';
import { WindowsTunnelSetupComponent } from './windows-tunnel-setup';

describe('WindowsTunnelSetupComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WindowsTunnelSetupComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('renders keeper/watchdog status once the probe resolves', () => {
    const fixture = TestBed.createComponent(WindowsTunnelSetupComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/management/windows-tunnel/status').flush(healthy());
    fixture.detectChanges();

    const root: HTMLElement = fixture.nativeElement.querySelector('[data-testid="windows-tunnel-setup"]');
    expect(root.getAttribute('data-state')).toBe('ok');
    expect(root.textContent).toContain('Registered and healthy');
    expect(fixture.nativeElement.querySelector('[data-testid="windows-tunnel-keeper-state"]').textContent)
      .toContain('Ready');
  });

  it('shows a quiet not-applicable message on a non-Windows Studio host', () => {
    const fixture = TestBed.createComponent(WindowsTunnelSetupComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/management/windows-tunnel/status').flush({
      platform: 'unsupported', observedAt: '2026-08-18T09:00:00Z',
      keeperTask: null, keeperHealth: null, watchdogTask: null, watchdogHealth: null,
      alarmActive: false, detail: 'The Windows tunnel keeper and watchdog only run on a Windows control-plane host.',
    } satisfies WindowsTunnelStatus);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="windows-tunnel-setup"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="windows-tunnel-unsupported"]').textContent)
      .toContain('only run on a Windows control-plane host');
  });

  it('register() posts the request and surfaces the elevation-declined result without re-polling', () => {
    const fixture = TestBed.createComponent(WindowsTunnelSetupComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/management/windows-tunnel/status').flush(healthy());
    fixture.detectChanges();

    const button: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="windows-tunnel-register"]');
    button.click();
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Waiting for Windows consent');

    const registerRequest = httpMock.expectOne('/api/v1/management/windows-tunnel/register');
    registerRequest.flush({
      platform: 'windows', ok: false, elevated: false,
      detail: 'Elevation was declined at the Windows consent prompt. No scheduled task was registered.',
      requestedAt: '2026-08-18T09:00:00Z',
    });
    fixture.detectChanges();

    const result: HTMLElement = fixture.nativeElement.querySelector('[data-testid="windows-tunnel-register-result"]');
    expect(result.getAttribute('data-ok')).toBe('false');
    expect(result.textContent).toContain('Elevation was declined');
    expect(button.disabled).toBe(false);
  });

  it('register() re-polls status once the registration succeeds', () => {
    const fixture = TestBed.createComponent(WindowsTunnelSetupComponent);
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/management/windows-tunnel/status').flush(healthy());
    fixture.detectChanges();

    fixture.nativeElement.querySelector('[data-testid="windows-tunnel-register"]').click();
    fixture.detectChanges();
    httpMock.expectOne('/api/v1/management/windows-tunnel/register').flush({
      platform: 'windows', ok: true, elevated: true,
      detail: 'Scheduled tasks registered: keeper registered, watchdog registered.',
      requestedAt: '2026-08-18T09:00:00Z',
    });
    fixture.detectChanges();

    httpMock.expectOne('/api/v1/management/windows-tunnel/status').flush(healthy());
    const result: HTMLElement = fixture.nativeElement.querySelector('[data-testid="windows-tunnel-register-result"]');
    expect(result.getAttribute('data-ok')).toBe('true');
  });
});

function healthy(): WindowsTunnelStatus {
  return {
    platform: 'windows',
    observedAt: '2026-08-18T09:00:00Z',
    keeperTask: {
      taskName: 'AgentRunner-TunnelKeeper', registered: true, state: 'Ready',
      lastRunTime: '2026-08-18T08:55:00Z', lastTaskResult: 0, nextRunTime: '2026-08-18T09:00:00Z',
    },
    keeperHealth: { status: 'healthy', message: null, observedAt: '2026-08-18T08:55:00Z', repairAttempts: 0 },
    watchdogTask: {
      taskName: 'AgentRunner-TunnelWatchdog', registered: true, state: 'Running',
      lastRunTime: '2026-08-18T08:59:00Z', lastTaskResult: null, nextRunTime: null,
    },
    watchdogHealth: {
      lastHealSucceededAt: '2026-08-18T08:40:00Z', lastHealFailedAt: null,
      lastProbeFailedAt: null, lastEvent: 'heal_succeeded', lastEventAt: '2026-08-18T08:40:00Z',
    },
    alarmActive: false,
    detail: null,
  };
}
