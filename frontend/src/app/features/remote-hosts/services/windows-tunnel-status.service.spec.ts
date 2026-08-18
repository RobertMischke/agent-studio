import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { WINDOWS_TUNNEL_DEFAULTS, type WindowsTunnelStatus } from '../models/windows-tunnel.model';
import { WindowsTunnelStatusService } from './windows-tunnel-status.service';

describe('WindowsTunnelStatusService', () => {
  let service: WindowsTunnelStatusService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(WindowsTunnelStatusService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    service.stop();
    httpMock.verify();
  });

  it('refresh() populates status on success and clears any prior error', () => {
    service.refresh();
    const request = httpMock.expectOne('/api/v1/management/windows-tunnel/status');
    expect(request.request.method).toBe('GET');
    request.flush(sample());

    expect(service.status()?.platform).toBe('windows');
    expect(service.loading()).toBe(false);
    expect(service.error()).toBeNull();
  });

  it('refresh() records a reachability error without throwing', () => {
    service.refresh();
    const request = httpMock.expectOne('/api/v1/management/windows-tunnel/status');
    request.error(new ProgressEvent('network error'));

    expect(service.error()).toContain('Could not reach');
    expect(service.loading()).toBe(false);
  });

  it('register() posts the request body and returns the parsed response', () => {
    let response: unknown;
    service.register(WINDOWS_TUNNEL_DEFAULTS).subscribe(value => (response = value));

    const request = httpMock.expectOne('/api/v1/management/windows-tunnel/register');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(WINDOWS_TUNNEL_DEFAULTS);
    request.flush({ platform: 'windows', ok: true, elevated: true, detail: 'ok', requestedAt: '2026-08-18T09:00:00Z' });

    expect(response).toMatchObject({ ok: true, elevated: true });
  });
});

function sample(): WindowsTunnelStatus {
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
