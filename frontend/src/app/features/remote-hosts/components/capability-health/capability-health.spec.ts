import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { CapabilityHealthComponent } from './capability-health';
import type { RemoteHost } from '../../models/remote-host.model';

describe('CapabilityHealthComponent', () => {
  it('distinguishes automatic drain and exposes recovery facts', () => {
    TestBed.configureTestingModule({ imports: [CapabilityHealthComponent] });
    const fixture = TestBed.createComponent(CapabilityHealthComponent);
    fixture.componentRef.setInput('host', {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'draining', os: 'Linux', lastHeartbeatAt: new Date().toISOString(),
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      hostAdmission: {
        hostId: 'host', admissionState: 'automatic-draining',
        automaticDrainReason: 'host:disk: DiskFull',
      },
      capabilityHealth: [{
        key: 'host:disk', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'draining', reason: 'DiskFull', advertisedAt: new Date().toISOString(),
        freshUntil: new Date().toISOString(), isFresh: true, consecutiveFailures: 1,
        canaryClaimId: null, affectedClaims: ['run:one'], recoveryHistory: [],
      }],
    } satisfies RemoteHost);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Automatic whole-host drain');
    expect(text).toContain('host:disk');
    expect(text).toContain('run:one');
    expect(text).not.toContain('Operator-requested');
  });
});
