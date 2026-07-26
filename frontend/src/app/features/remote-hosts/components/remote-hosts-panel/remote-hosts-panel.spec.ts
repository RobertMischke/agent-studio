import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RemoteHostsPanelComponent } from './remote-hosts-panel';

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
});
