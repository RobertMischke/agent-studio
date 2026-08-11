import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { AutoReviewQueueTelemetryStore } from '../../../../services/auto-review-queue-telemetry.store';

/**
 * Render-path test: the panel seeds its registry on init and renders one card
 * per host with a summary line whose counts reconcile to the visible rows
 * (R3 sum invariant).
 */
describe('RemoteHostsPanelComponent', () => {
  it('mounts, seeds the registry, and renders a card per host', async () => {
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
    TestBed.inject(AutoReviewQueueTelemetryStore).status.set({
      queueDepth: 40, activeReviews: 4, outstandingReviews: 44,
      completedReviewsInRateWindow: 9, drainRatePerHour: 9,
      medianReviewDurationSeconds: 750, reviewDurationSampleCount: 21,
      oldestQueuedAt: '2026-08-11T16:00:00Z', lastDrainAt: '2026-08-11T17:55:00Z',
      observedAt: '2026-08-11T18:00:00Z', rateWindowMinutes: 60,
      durationWindowMinutes: 1440, stagnantThresholdMinutes: 30,
      isStagnant: false, stagnantSince: null,
    });
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="remote-hosts-panel"]')).toBeTruthy();
    expect(el.querySelector('h2')?.textContent).toContain('Execution Hosts');
    const cards = el.querySelectorAll('[data-testid="remote-host-card"]');
    expect(cards.length).toBe(fixture.componentInstance.total());
    expect(cards.length).toBeGreaterThanOrEqual(2);
    expect(cards.item(0).querySelector('[data-role="local"]')?.textContent).toContain('Local');
    expect(cards.item(0).querySelector('[data-testid="remote-host-name"]')?.textContent)
      .toContain('Local machine');

    // Summary total equals the number of rendered cards (R3).
    const summary = el.querySelector('[data-testid="remote-hosts-summary"]')?.textContent ?? '';
    expect(summary).toContain(String(cards.length));
    expect(el.querySelector('[data-testid="auto-review-queue-depth"]')?.textContent)
      .toContain('40');

    const setupButton = el.querySelector('[data-testid="remote-host-action-setup"]') as HTMLButtonElement;
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
