import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskServerService } from './task-server.service';

/**
 * Service behaviour: seeds a snapshot on first load, keeps the live origin as
 * the connected URL, and applies the management sweeps optimistically with a
 * short delay before a result row lands.
 */
describe('TaskServerService', () => {
  let service: TaskServerService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), TaskServerService] });
    service = TestBed.inject(TaskServerService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('seeds a snapshot with clients and a live connected URL', () => {
    service.ensureLoaded();
    const status = service.status();
    expect(status).not.toBeNull();
    expect(status!.clients.length).toBeGreaterThanOrEqual(2);
    // window.location.origin is defined under jsdom; the URL is the live origin.
    expect(status!.connection.url).toBe(window.location.origin);
    expect(status!.recentResults).toEqual([]);
  });

  it('runs an archive sweep optimistically: busy, then a result row', () => {
    service.ensureLoaded();
    service.archiveSweep();
    expect(service.busyAction()).toBe('archive-sweep');
    expect(service.recentResults().length).toBe(0);

    vi.advanceTimersByTime(700);

    expect(service.busyAction()).toBeNull();
    const results = service.recentResults();
    expect(results.length).toBe(1);
    expect(results[0].kind).toBe('archive-sweep');
    expect(results[0].affected).toBeGreaterThan(0);
  });

  it('ignores a second sweep while one is in flight', () => {
    service.ensureLoaded();
    service.archiveSweep();
    service.orphanScan(); // ignored - archive-sweep still busy
    expect(service.busyAction()).toBe('archive-sweep');

    vi.advanceTimersByTime(700);
    expect(service.recentResults().length).toBe(1);
    expect(service.recentResults()[0].kind).toBe('archive-sweep');
  });

  it('preserves session results across a reload', () => {
    service.ensureLoaded();
    service.fixtureCleanup();
    vi.advanceTimersByTime(700);
    expect(service.recentResults().length).toBe(1);

    service.reload();
    expect(service.recentResults().length).toBe(1);
  });
});
