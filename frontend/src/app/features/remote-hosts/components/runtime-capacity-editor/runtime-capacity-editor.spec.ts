import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import {
  RuntimeCapacityEditorComponent,
  type RuntimeCapacityChange,
} from './runtime-capacity-editor';
import type { RemoteHost } from '../../models/remote-host.model';

const HOST: RemoteHost = {
  id: 'runner-a',
  name: 'Runner A',
  role: 'remote',
  address: null,
  clientId: 'runner-a',
  status: 'online',
  os: 'Linux',
  lastHeartbeatAt: new Date().toISOString(),
  uptimeLabel: null,
  capabilities: [],
  cliQuotas: [],
  stats: null,
  activeTaskCount: 2,
  effectiveMaxParallelism: 4,
  runtimeCapacityAppliedAt: new Date().toISOString(),
  runtimeCapacity: {
    hostId: 'host-a',
    maxParallelism: 4,
    targetLoadPercent: 80,
    rampStrategy: 'balanced',
    version: 1,
    updatedAt: new Date().toISOString(),
  },
};

describe('RuntimeCapacityEditorComponent', () => {
  it('shows the central total and emits a validated capacity update', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', HOST);
    let change: RuntimeCapacityChange | null = null;
    fixture.componentInstance.capacityChange.subscribe(value => { change = value; });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('[data-testid="remote-host-slots"]')?.textContent)
      .toContain('2 active / 2 free / 4 total');
    (element.querySelector('[data-testid="remote-host-capacity-input"]') as HTMLInputElement).value = '6';
    (element.querySelector('[data-testid="remote-host-capacity-input"]') as HTMLInputElement)
      .dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="remote-host-target-load-input"]') as HTMLInputElement).value = '85';
    (element.querySelector('[data-testid="remote-host-target-load-input"]') as HTMLInputElement)
      .dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="remote-host-ramp-select"]') as HTMLSelectElement).value = 'aggressive';
    (element.querySelector('[data-testid="remote-host-ramp-select"]') as HTMLSelectElement)
      .dispatchEvent(new Event('change'));
    (element.querySelector('[data-testid="remote-host-capacity-save"]') as HTMLButtonElement).click();

    expect(change).toEqual({
      id: 'runner-a',
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'aggressive',
    });
  });

  it('marks a central change as waiting until the daemon reports adoption', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      runtimeCapacity: { ...HOST.runtimeCapacity!, maxParallelism: 6, version: 2 },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="remote-host-capacity-awaiting-adoption"]',
    )).toBeTruthy();
  });
});
