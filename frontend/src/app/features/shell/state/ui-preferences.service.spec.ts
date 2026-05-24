import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { UiPreferencesService } from './ui-preferences.service';

/**
 * F24 regression. Two pieces locked here:
 *
 *  - the constructor reads the persisted keys from localStorage so a
 *    reload lands on the last-saved posture.
 *  - the `storage` event - the only signal the browser gives a tab
 *    that a SIBLING tab wrote localStorage - mirrors that write back
 *    into the local signals so two open tabs converge on the same
 *    posture without a page reload.
 *
 * The browser only fires `storage` in OTHER tabs, never in the writing
 * tab; we simulate that "other tab" by dispatching a synthetic
 * StorageEvent against window. The service mutates the signal; the
 * test reads it back.
 */
describe('UiPreferencesService', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('reads compactCards from localStorage on construction', () => {
    localStorage.setItem('compactCards', '1');
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.compactCards()).toBe(true);
  });

  it('toggleCompactCards writes to localStorage and updates the signal', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.compactCards()).toBe(false);
    svc.toggleCompactCards();
    expect(svc.compactCards()).toBe(true);
    expect(localStorage.getItem('compactCards')).toBe('1');
    svc.toggleCompactCards();
    expect(svc.compactCards()).toBe(false);
    expect(localStorage.getItem('compactCards')).toBe('0');
  });

  it('setCompactCards persists an explicit value (F43 callers that know the next effective state)', () => {
    const svc = TestBed.inject(UiPreferencesService);
    svc.setCompactCards(true);
    expect(svc.compactCards()).toBe(true);
    expect(localStorage.getItem('compactCards')).toBe('1');
    svc.setCompactCards(false);
    expect(svc.compactCards()).toBe(false);
    expect(localStorage.getItem('compactCards')).toBe('0');
    // Idempotent: setting the same value twice keeps both the signal
    // and the storage row stable.
    svc.setCompactCards(false);
    expect(svc.compactCards()).toBe(false);
    expect(localStorage.getItem('compactCards')).toBe('0');
  });

  it('userOverridesCompactWhileRail is in-memory only, not persisted, not storage-synced (F43)', () => {
    const svc = TestBed.inject(UiPreferencesService);
    // Default: no override.
    expect(svc.userOverridesCompactWhileRail()).toBe(false);
    // Set it — signal updates, but no localStorage row is written. A
    // sibling tab whose rail is closed should not inherit the override.
    svc.userOverridesCompactWhileRail.set(true);
    expect(svc.userOverridesCompactWhileRail()).toBe(true);
    expect(localStorage.getItem('userOverridesCompactWhileRail')).toBeNull();
    // A sibling tab dispatching a storage event for the override key is
    // ignored — the service only mirrors the documented persistent keys.
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'userOverridesCompactWhileRail',
        newValue: '0',
        oldValue: '1',
        storageArea: localStorage,
      }),
    );
    expect(svc.userOverridesCompactWhileRail()).toBe(true);
  });

  it('mirrors a sibling tab compactCards write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.compactCards()).toBe(false);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'compactCards',
        newValue: '1',
        oldValue: '0',
        storageArea: localStorage,
      }),
    );
    expect(svc.compactCards()).toBe(true);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'compactCards',
        newValue: '0',
        oldValue: '1',
        storageArea: localStorage,
      }),
    );
    expect(svc.compactCards()).toBe(false);
  });

  it('mirrors a sibling tab taskNavCollapsed write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.taskNavCollapsed()).toBe(false);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'taskNavCollapsed',
        newValue: '1',
        storageArea: localStorage,
      }),
    );
    expect(svc.taskNavCollapsed()).toBe(true);
  });

  it('mirrors a sibling tab sideSheetWidth write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.sideSheetWidth()).toBe(280);

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'sideSheetWidth',
        newValue: '420',
        storageArea: localStorage,
      }),
    );
    expect(svc.sideSheetWidth()).toBe(420);
  });

  it('ignores storage events targeting unrelated keys', () => {
    const svc = TestBed.inject(UiPreferencesService);
    const before = {
      compact: svc.compactCards(),
      nav: svc.taskNavCollapsed(),
      width: svc.sideSheetWidth(),
    };
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'someUnrelatedKey',
        newValue: 'whatever',
        storageArea: localStorage,
      }),
    );
    expect(svc.compactCards()).toBe(before.compact);
    expect(svc.taskNavCollapsed()).toBe(before.nav);
    expect(svc.sideSheetWidth()).toBe(before.width);
  });
});
