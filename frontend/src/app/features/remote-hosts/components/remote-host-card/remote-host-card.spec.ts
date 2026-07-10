import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostCardComponent } from './remote-host-card';
import type { RemoteHost } from '../../models/remote-host.model';

const HOST: RemoteHost = {
  id: 'hetzner',
  name: 'agent-runner',
  role: 'remote',
  address: 'ssh://agent@runner.hetzner',
  status: 'online',
  os: 'Ubuntu 24.04 LTS',
  lastHeartbeatAt: '2026-07-10T11:59:55Z',
  uptimeLabel: '2d 9h',
  capabilities: ['linux', 'playwright'],
  cliQuotas: [{ cliType: 'claude', plan: 'Max', windowLabel: '5h', usedPct: 63, resetLabel: 'in 1h' }],
  stats: {
    ramTotalMb: 62 * 1024,
    ramFreeMb: 38 * 1024,
    cpuCores: 8,
    cpuModel: 'Xeon',
    cpuLoadPct: 54,
    diskTotalGb: 240,
    diskFreeGb: 96,
  },
};

function mount(host: RemoteHost) {
  TestBed.configureTestingModule({
    imports: [RemoteHostCardComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(RemoteHostCardComponent);
  fixture.componentRef.setInput('host', host);
  fixture.componentRef.setInput('now', Date.parse('2026-07-10T12:00:00Z'));
  fixture.detectChanges();
  return fixture;
}

describe('RemoteHostCardComponent', () => {
  it('renders name, status badge, role, and the three vitals meters', () => {
    const el: HTMLElement = mount(HOST).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-name"]')?.textContent).toContain('agent-runner');
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Online');
    expect(el.querySelector('.host__role')?.textContent).toContain('Remote');
    expect(el.querySelectorAll('.meter').length).toBe(3);
    // RAM 24/62 GB used => 39%
    expect(el.querySelector('[data-meter="ram"] .meter__pct')?.textContent).toContain('39%');
  });

  it('reflects an acute status as a data-tone on the host element', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'offline' }).nativeElement;
    // The host binding lands on the component host element itself.
    expect(el.getAttribute('data-tone')).toBe('error');
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Offline');
  });

  it('emits an action event with the host id when a control is clicked', () => {
    const fixture = mount(HOST);
    let received: { kind: string; id: string } | null = null;
    fixture.componentInstance.action.subscribe((e) => (received = e));
    const btn = fixture.nativeElement.querySelector('[data-testid="remote-host-action-drain"]') as HTMLButtonElement;
    btn.click();
    expect(received).toEqual({ kind: 'drain', id: 'hetzner' });
  });

  it('renders "no stats" when a host reports none (e.g. retired)', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'retired', stats: null }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-no-stats"]')).toBeTruthy();
    expect(el.querySelectorAll('.meter').length).toBe(0);
  });
});
