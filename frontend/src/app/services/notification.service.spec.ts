import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { NotificationService } from './notification.service';

describe('NotificationService', () => {
  let service: NotificationService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    service = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('appends each notify call to the stack', () => {
    service.success('saved');
    service.error('oops');
    expect(service.notifications().map((n) => n.kind)).toEqual(['success', 'error']);
  });

  it('auto-dismisses after the per-kind default duration', () => {
    service.success('saved');
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(3499);
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(2);
    expect(service.notifications()).toHaveLength(0);
  });

  it('respects an explicit durationMs override', () => {
    service.notify({ message: 'sticky', kind: 'info', durationMs: 10_000 });
    vi.advanceTimersByTime(5_000);
    expect(service.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(5_001);
    expect(service.notifications()).toHaveLength(0);
  });

  it('does NOT auto-dismiss when durationMs is 0', () => {
    const id = service.notify({ message: 'sticky', kind: 'info', durationMs: 0 });
    vi.advanceTimersByTime(60_000);
    expect(service.notifications()).toHaveLength(1);
    service.dismiss(id);
    expect(service.notifications()).toHaveLength(0);
  });

  it('dismiss removes the notification and cancels its timer', () => {
    const id = service.success('saved');
    service.dismiss(id);
    expect(service.notifications()).toHaveLength(0);
    // Advancing timers should be a no-op now.
    vi.advanceTimersByTime(10_000);
    expect(service.notifications()).toHaveLength(0);
  });

  it('dismissAll empties the stack', () => {
    service.info('a');
    service.warning('b');
    service.error('c');
    service.dismissAll();
    expect(service.notifications()).toHaveLength(0);
  });
});
