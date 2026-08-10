import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BoardFiltersService } from '../../state/board-filters.service';
import { ActiveBoardFiltersComponent } from './active-board-filters.component';

describe('ActiveBoardFiltersComponent', () => {
  let filters: BoardFiltersService;

  beforeEach(async () => {
    localStorage.clear();
    history.replaceState(null, '', '/#/board');
    await TestBed.configureTestingModule({
      imports: [ActiveBoardFiltersComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    filters = TestBed.inject(BoardFiltersService);
  });

  it('keeps the search chip identity stable while its visible query changes', async () => {
    filters.setSearchQuery('first query');
    const fixture = TestBed.createComponent(ActiveBoardFiltersComponent);
    fixture.componentRef.setInput('resultCount', 2);
    fixture.detectChanges();
    await fixture.whenStable();
    const firstChip = fixture.nativeElement.querySelector('[data-testid="board-active-filter-chip"]');
    expect(firstChip?.textContent).toContain('Search: first query');

    filters.setSearchQuery('second query');
    fixture.detectChanges();
    await fixture.whenStable();
    const updatedChip = fixture.nativeElement.querySelector('[data-testid="board-active-filter-chip"]');

    expect(updatedChip).toBe(firstChip);
    expect(updatedChip?.textContent).toContain('Search: second query');
  });

  it('names a zero-result filter and removes it through the visible action', async () => {
    history.replaceState(null, '', '/#/board&filters=integration%3Astalled');
    filters.hydrateFromUrl();
    const fixture = TestBed.createComponent(ActiveBoardFiltersComponent);
    fixture.componentRef.setInput('resultCount', 0);
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="board-active-filter-chip"]')?.textContent)
      .toContain('integration:stalled');
    expect(host.querySelector('[data-testid="board-filter-empty-hint"]')?.textContent)
      .toContain('0 tasks for filter integration:stalled');

    (host.querySelector('[data-testid="board-filter-empty-hint-clear"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(filters.stalledIntegrationOnly()).toBe(false);
    expect(decodeURIComponent(window.location.hash)).toBe('#/board');
  });
});
