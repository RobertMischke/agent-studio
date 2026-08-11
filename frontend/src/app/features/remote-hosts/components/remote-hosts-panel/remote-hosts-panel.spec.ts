import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';
import { RemoteHostsService } from '../../services/remote-hosts.service';

/**
 * Render-path test: the panel seeds its registry on init and renders one table
 * row per host with a summary line whose counts reconcile to the visible rows
 * (R3 sum invariant).
 */
describe('RemoteHostsPanelComponent', () => {
  it('mounts, seeds the registry, and renders a sortable row per host', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-panel"]')).toBeTruthy();
    expect(el.querySelector('h2')?.textContent).toContain('Execution Hosts');
    const cards = el.querySelectorAll('[data-testid="remote-host-card"]');
    expect(cards.length).toBe(fixture.componentInstance.total());
    expect(cards.length).toBeGreaterThanOrEqual(2);
    const local = el.querySelector('[data-host="local"]');
    expect(local?.querySelector('[data-testid="remote-host-name"]')?.textContent)
      .toContain('Local machine');
    expect(el.querySelector('[data-testid="remote-hosts-table"]')).toBeTruthy();

    // Summary total equals the number of rendered cards (R3).
    const summary = el.querySelector('[data-testid="remote-hosts-summary"]')?.textContent ?? '';
    expect(summary).toContain(String(cards.length));

    const remote = el.querySelector('[data-host="agent-runner-01"]') as HTMLElement;
    (remote.querySelector('[data-testid="remote-host-disclosure"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const setupButton = remote.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement;
    setupButton.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.setupHost()?.id).toBe('agent-runner-01');
    expect(el.querySelector('[data-testid="runner-setup-dialog"]')).toBeTruthy();
    fixture.componentInstance.closeSetup();
    fixture.detectChanges();

    const addButton = el.querySelector('[data-testid="remote-hosts-add"]') as HTMLButtonElement;
    addButton.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="add-host-wizard"]')).toBeTruthy();
    expect(el.querySelector('#add-host-title')?.textContent).toContain('Add an execution host');

    fixture.destroy();
  });

  it('renders the corrupt identity recovery diagnostic', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const service = TestBed.inject(RemoteHostsService);
    service.identityDiagnostics.set([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'service', registeredAt: '2026-08-05T14:35:00Z', lastSeenAt: null,
      tokenBudgetMonthly: null, notes: null,
      identityFileError: 'identity file corrupt: agent-runner-01.json',
      identityFileName: 'agent-runner-01.json',
      identityFileModifiedAt: '2026-08-05T14:35:00Z',
      identityRestoreHint: 'Restore a valid file or re-register with POST /api/clients/register.',
    }]);

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const diagnostic = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="remote-hosts-identity-errors"]',
    );

    expect(diagnostic?.textContent).toContain('identity file corrupt: agent-runner-01.json');
    expect(diagnostic?.textContent).toContain('POST /api/clients/register');
    fixture.destroy();
  });
});
