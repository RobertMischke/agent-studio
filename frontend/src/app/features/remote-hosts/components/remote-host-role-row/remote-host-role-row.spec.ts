import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { RemoteHostRoleRowComponent, roleSlotTotal } from './remote-host-role-row';

const REVIEW: RemoteHost = {
  id: 'review-a',
  name: 'agent-runner-01-review',
  role: 'remote',
  serviceRole: 'review',
  roleMaxParallelism: 6,
  address: null,
  clientId: 'review-a',
  status: 'online',
  os: 'Linux',
  lastHeartbeatAt: '2026-08-12T09:00:00Z',
  uptimeLabel: null,
  capabilities: [],
  cliQuotas: [],
  stats: null,
};

describe('RemoteHostRoleRowComponent', () => {
  it('renders review capacity from the role-local runner ceiling', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', REVIEW);
    fixture.componentRef.setInput('activeSlots', 2);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-role-label"]')?.textContent)
      .toContain('Review');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-role-slots"]')?.textContent)
      .toContain('2 / 6');
  });

  it('keeps review role capacity separate from the physical host policy', () => {
    expect(roleSlotTotal({
      ...REVIEW,
      runtimeCapacity: {
        hostId: 'host-a', maxParallelism: 20, targetLoadPercent: 80,
        rampStrategy: 'balanced', version: 1, updatedAt: '2026-08-12T09:00:00Z',
      },
    })).toBe(6);
  });

  it('renders one quiet dash when a role ceiling is not reported', () => {
    TestBed.configureTestingModule({
      imports: [RemoteHostRoleRowComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(RemoteHostRoleRowComponent);
    fixture.componentRef.setInput('host', { ...REVIEW, roleMaxParallelism: null });
    fixture.detectChanges();

    const slots = fixture.nativeElement.querySelector('[data-testid="remote-host-role-slots"]');
    expect(slots?.textContent).toContain('–');
    expect(slots?.textContent).not.toContain('n/a');
  });
});
