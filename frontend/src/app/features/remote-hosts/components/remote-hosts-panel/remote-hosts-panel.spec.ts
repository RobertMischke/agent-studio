import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { ReviewQueueTelemetryStore } from '../../../../services/review-queue-telemetry.store';

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

  it('renders queue depth, drain rate, and median review duration', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteHostsPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const reviewQueue = TestBed.inject(ReviewQueueTelemetryStore);
    reviewQueue.snapshot.set({
      observedAt: '2026-08-11T22:00:00Z', queueDepth: 40, waitingDepth: 36,
      activeReviews: 4, drainRatePerHour: 3.5, drainWindowMinutes: 60,
      medianReviewDurationSeconds: 1_200, durationWindowHours: 24,
      durationSampleCount: 20, lastDrainAt: '2026-08-11T21:55:00Z',
      oldestWaitingAt: '2026-08-11T20:00:00Z', stagnant: false,
      stagnationThresholdMinutes: 30, stagnantForMinutes: 0,
    });

    const fixture = TestBed.createComponent(RemoteHostsPanelComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="remote-hosts-review-depth"]')?.textContent).toContain('40');
    expect(el.querySelector('[data-testid="remote-hosts-review-drain"]')?.textContent).toContain('3.5/h');
    expect(el.querySelector('[data-testid="remote-hosts-review-duration"]')?.textContent).toContain('20 min');
    fixture.destroy();
  });
});
