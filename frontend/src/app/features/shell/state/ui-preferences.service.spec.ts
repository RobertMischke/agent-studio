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

  it('AGT-2035 migration: clears the abolished compactCards key on construction', () => {
    localStorage.setItem('compactCards', '1');
    TestBed.inject(UiPreferencesService);
    expect(localStorage.getItem('compactCards')).toBeNull();
  });

  it('defaults tree metrics to numbers and persists the experimental dot view', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.treeMetricView()).toBe('numbers');
    svc.setTreeMetricView('dots');
    expect(svc.treeMetricView()).toBe('dots');
    expect(localStorage.getItem('atp.studio.explorer.metrics')).toBe('dots');
  });

  it('mirrors a sibling tab tree metric write via the storage event', () => {
    const svc = TestBed.inject(UiPreferencesService);
    expect(svc.treeMetricView()).toBe('numbers');

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'atp.studio.explorer.metrics',
        newValue: 'dots',
        storageArea: localStorage,
      }),
    );
    expect(svc.treeMetricView()).toBe('dots');

    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'atp.studio.explorer.metrics',
        newValue: 'numbers',
        storageArea: localStorage,
      }),
    );
    expect(svc.treeMetricView()).toBe('numbers');
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
      nav: svc.taskNavCollapsed(),
      width: svc.sideSheetWidth(),
      metrics: svc.treeMetricView(),
    };
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: 'someUnrelatedKey',
        newValue: 'whatever',
        storageArea: localStorage,
      }),
    );
    expect(svc.taskNavCollapsed()).toBe(before.nav);
    expect(svc.sideSheetWidth()).toBe(before.width);
    expect(svc.treeMetricView()).toBe(before.metrics);
  });
});
