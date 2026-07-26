import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { RemoteHostCardComponent } from './remote-host-card';
import type { RemoteHost } from '../../models/remote-host.model';

const HOST: RemoteHost = {
  id: 'hetzner',
  name: 'agent-runner',
  role: 'remote',
  address: 'ssh://agent@runner.hetzner',
  clientId: 'agent-runner',
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
  activeTaskCount: 1,
  availableSlots: 19,
  activeGateCount: 2,
  gateCapacity: 4,
};

function mount(host: RemoteHost) {
  TestBed.resetTestingModule();
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
    expect(el.querySelector('[data-testid="remote-host-run-pool"]')?.textContent).toContain('1 active · 19 free · 20 max');
    expect(el.querySelector('[data-testid="remote-host-gate-pool"]')?.textContent).toContain('2 running · pool 4');
    expect(el.querySelector('[data-testid="remote-host-cpu-context"]')?.textContent).toContain('does not consume a RUN slot');
  });

  it('shows a neutral live-loading state instead of cached stopped data', () => {
    const el: HTMLElement = mount({
      ...HOST,
      status: 'offline',
      lastHeartbeatAt: '2026-07-10T09:00:00Z',
      liveDataState: 'loading',
      stats: null,
    }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Loading live status');
    expect(el.querySelector('[data-testid="remote-host-run-pool"]')?.textContent).toContain('Loading daemon telemetry');
    expect(el.textContent).not.toContain('Daemonstopped');
    expect(el.querySelector('[data-testid="remote-host-stale"]')).toBeNull();
  });

  it('reflects an acute status as a data-tone on the host element', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'offline' }).nativeElement;
    // The host binding lands on the component host element itself.
    expect(el.getAttribute('data-tone')).toBe('error');
    expect(el.querySelector('[data-testid="remote-host-status"]')?.textContent).toContain('Offline');
  });

  it('shows the independent read-only badge when the startup push probe fails', () => {
    const fixture = mount({
      ...HOST,
      gitPushStatus: 'read-only',
      gitPushDetail: 'push-dry-run failed (128): permission denied',
    });
    const el: HTMLElement = fixture.nativeElement;
    const badge = el.querySelector('[data-testid="remote-host-git-status"]');
    expect(badge?.textContent).toContain('Writable: no');
    const tooltip = fixture.debugElement
      .query(By.css('[data-testid="remote-host-git-status"]'))
      .injector.get(AppTooltipDirective);
    expect(tooltip.appTooltip()).toContain('permission denied');
    expect(badge?.getAttribute('data-tone')).toBe('error');
  });

  it('shows the project and reason when a delivery preflight blocks claims', () => {
    const el: HTMLElement = mount({
      ...HOST,
      projectPreflights: [{
        projectId: 'PROJ-042', projectName: 'Payments', registrationFingerprint: 'a'.repeat(64),
        repositoryUrl: 'https://example.test/payments.git', fetchUrl: 'https://example.test/payments.git',
        pushUrl: 'https://example.test/payments.git', status: 'failed',
        detail: 'write probe failed: permission denied', checkedAt: '2026-07-10T11:59:00Z',
      }],
    }).nativeElement;
    const failure = el.querySelector('[data-testid="remote-host-project-preflight-failures"]');
    expect(failure?.textContent).toContain('Payments');
    expect(failure?.textContent).toContain('permission denied');
  });

  it('emits an action event with the host id when a control is clicked', () => {
    const fixture = mount(HOST);
    let received: { kind: string; id: string } | null = null;
    fixture.componentInstance.action.subscribe((e) => (received = e));
    const btn = fixture.nativeElement.querySelector('[data-testid="remote-host-action-drain"]') as HTMLButtonElement;
    btn.click();
    expect(received).toEqual({ kind: 'drain', id: 'hetzner' });
  });

  it('offers setup only for active remote hosts and emits the selected host', () => {
    const remote = mount(HOST);
    let selected: RemoteHost | null = null;
    remote.componentInstance.setup.subscribe(host => { selected = host; });

    const setup = remote.nativeElement.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement;
    expect(setup).toBeTruthy();
    setup.click();
    expect(selected).toEqual(HOST);

    const local = mount({ ...HOST, id: 'local', role: 'local', address: null });
    expect(local.nativeElement.querySelector('[data-testid="remote-host-action-setup"]')).toBeNull();
  });

  it('renders "no stats" when a host reports none (e.g. retired)', () => {
    const el: HTMLElement = mount({ ...HOST, status: 'retired', stats: null }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-no-stats"]')).toBeTruthy();
    expect(el.querySelectorAll('.meter').length).toBe(0);
  });

  it('hides stale metrics instead of presenting the last CPU value as live', () => {
    const el: HTMLElement = mount({ ...HOST, lastHeartbeatAt: '2026-07-08T12:00:00Z' }).nativeElement;
    expect(el.querySelector('[data-testid="remote-host-stale"]')?.textContent).toContain('last seen 2d ago');
    expect(el.querySelectorAll('.meter').length).toBe(0);
    expect(el.textContent).not.toContain('54%');
  });

  it('renders telemetry charts, slot context, findings, and switches windows', () => {
    const telemetry = {
      clientId: 'agent-runner', window: '14d',
      points: [
        { timestamp: '2026-07-10T11:00:00Z', cpuPercent: 42, load1: 5.8, load5: 5, load15: 4,
          memoryUsedBytes: 32e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
          cpuStealPercent: 6, ioWaitPercent: 2, cpuCores: 12, activeSlots: 5 },
        { timestamp: '2026-07-10T11:59:30Z', cpuPercent: 54, load1: 6.4, load5: 6, load15: 5,
          memoryUsedBytes: 34e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
          cpuStealPercent: 7, ioWaitPercent: 3, cpuCores: 12, activeSlots: 6 },
      ],
      findings: [{ kind: 'vm-throttled' as const, label: 'VM throttled', since: '2026-07-10T11:58:00Z', until: '2026-07-10T11:59:30Z' }],
    };
    const fixture = mount({ ...HOST, telemetry });
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('[data-chart]').length).toBe(4);
    expect(el.querySelector('[data-testid="remote-host-slots-context"]')?.textContent).toContain('6 RUN active · host load 6.4 of 12 cores');
    expect(el.querySelector('[data-testid="remote-host-findings"]')?.textContent).toContain('VM throttled');
    (el.querySelector('[data-testid="remote-host-window-1h"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.telemetryWindow()).toBe('1h');
  });

  it('shows the exact synchronized telemetry point selected on the shared plot', () => {
    const points = [
      { timestamp: '2026-07-10T11:00:00Z', cpuPercent: 42, load1: 5.8, load5: 5, load15: 4,
        memoryUsedBytes: 32e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
        cpuStealPercent: 6, ioWaitPercent: 2, cpuCores: 12, activeSlots: 5 },
      { timestamp: '2026-07-10T11:30:00Z', cpuPercent: 54.25, load1: 6.4, load5: 6, load15: 5,
        memoryUsedBytes: 34.5e9, memoryTotalBytes: 64e9, swapInBytesPerSecond: 0, swapOutBytesPerSecond: 0,
        cpuStealPercent: 7, ioWaitPercent: 3, cpuCores: 12, activeSlots: 6 },
    ];
    const fixture = mount({ ...HOST, telemetry: { clientId: 'agent-runner', window: '14d', points, findings: [] } });
    const plots = fixture.nativeElement.querySelector('[data-testid="remote-host-telemetry-plots"]') as HTMLElement;
    plots.getBoundingClientRect = () => ({ left: 100, width: 200 } as DOMRect);

    fixture.componentInstance.showTelemetryPoint({ currentTarget: plots, clientX: 290 } as unknown as PointerEvent);
    fixture.detectChanges();

    const tooltip = fixture.nativeElement.querySelector('[data-testid="remote-host-telemetry-tooltip"]') as HTMLElement;
    expect(tooltip.dataset['pointTimestamp']).toBe(points[1].timestamp);
    expect(tooltip.querySelector('[data-metric="cpu"]')?.textContent).toContain('54.25%');
    expect(tooltip.querySelector('[data-metric="memory"]')?.textContent).toContain('34.5 GB');
    expect(tooltip.querySelector('[data-metric="load"]')?.textContent).toContain('6.4 load');
    expect(tooltip.querySelector('[data-metric="slots"]')?.textContent).toContain('6 slots');
    expect(fixture.nativeElement.querySelectorAll('.telemetry__point').length).toBe(4);

    fixture.componentInstance.hideTelemetryPoint({ pointerType: 'touch' } as PointerEvent);
    expect(fixture.componentInstance.hoveredTelemetryIndex()).toBe(1);
    fixture.componentInstance.hideTelemetryPoint({ pointerType: 'mouse' } as PointerEvent);
    expect(fixture.componentInstance.hoveredTelemetryIndex()).toBeNull();
  });
});
