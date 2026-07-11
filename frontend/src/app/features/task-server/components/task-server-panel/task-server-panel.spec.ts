import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskServerPanelComponent } from './task-server-panel';

/**
 * Render-path test: the panel seeds its status on init and renders the
 * connection / store / evidence blocks, a client-registry list, and the
 * management panel. The summary client count reconciles to the visible client
 * rows (R3 sum invariant), and running a sweep produces a result row.
 */
describe('TaskServerPanelComponent', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [TaskServerPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskServerPanelComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('mounts, seeds the status, and renders every block', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-panel"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-connection"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-store"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-evidence"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="task-server-management"]')).toBeTruthy();

    // The connected URL is the live origin.
    expect(el.querySelector('[data-testid="task-server-url"]')?.textContent)
      .toContain(window.location.origin);

    fixture.destroy();
  });

  it('summary client count reconciles to the visible client rows (R3)', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    const rows = el.querySelectorAll('[data-testid="task-server-clients"] > li');
    expect(rows.length).toBeGreaterThanOrEqual(2);

    const summary = el.querySelector('[data-testid="task-server-summary"]')?.textContent ?? '';
    expect(summary).toContain(String(rows.length));

    const sectionCount = el.querySelector('[data-testid="task-server-clients-section"] .ts__section-count')?.textContent ?? '';
    expect(sectionCount).toContain(String(rows.length));

    fixture.destroy();
  });

  it('running a sweep records a result row', async () => {
    const fixture = await mount();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[data-testid="task-server-results-empty"]')).toBeTruthy();

    const btn = el.querySelector('[data-testid="task-server-action-archive-sweep"]') as HTMLButtonElement;
    btn.click();
    vi.advanceTimersByTime(700);
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="task-server-result-archive-sweep"]')).toBeTruthy();

    fixture.destroy();
  });
});
