import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { NotificationState } from '../models/app-dialog.model';
import type { UpdateStatus } from '../models/update-service.model';
import { NotificationService } from './notification.service';
import { UpdateClientService } from './update.service';
import { UpdateNotificationBridge } from './update-notification-bridge.service';

const DISMISSED_STORAGE_KEY = 'atp.update.dismissedRuns';

function status(overrides: Partial<UpdateStatus>): UpdateStatus {
  return {
    phase: 'idle',
    phaseLabel: null,
    message: null,
    currentRunId: null,
    startedAt: null,
    finishedAt: null,
    headLocal: '',
    headOrigin: null,
    behindBy: 0,
    pendingCommits: [],
    lastFetchAt: null,
    lastUpdateAt: null,
    lastSuccessAt: null,
    lastRunFinishedAt: null,
    lastRunHeadBefore: null,
    lastRunHeadAfter: null,
    isRunning: false,
    backendReachable: true,
    serviceVersion: '',
    productVersion: '',
    mode: 'manual',
    verificationFailures: null,
    autoRollbackEnabled: false,
    ...overrides,
  };
}

function isoAgo(ms: number): string {
  return new Date(Date.now() - ms).toISOString();
}

function setup() {
  const statusSignal = signal<UpdateStatus | null>(null);
  TestBed.configureTestingModule({
    providers: [
      {
        provide: UpdateClientService,
        useValue: { status: statusSignal, rollback: () => Promise.resolve() },
      },
      NotificationService,
      UpdateNotificationBridge,
    ],
  });
  // Instantiating the bridge registers its status() effect.
  const bridge = TestBed.inject(UpdateNotificationBridge);
  const notify = TestBed.inject(NotificationService);
  return { statusSignal, bridge, notify };
}

function errorToasts(notify: NotificationService): NotificationState[] {
  return notify.notifications().filter(n => n.kind === 'error');
}

describe('UpdateNotificationBridge — failed-run freshness + dismiss persistence', () => {
  beforeEach(() => {
    localStorage.removeItem(DISMISSED_STORAGE_KEY);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(DISMISSED_STORAGE_KEY);
  });

  it('does NOT toast a long-finished failed run on a fresh load', () => {
    const { statusSignal, notify } = setup();

    statusSignal.set(status({
      phase: 'failed',
      currentRunId: 'old-run',
      message: 'restart failed (rc=1)',
      lastRunFinishedAt: isoAgo(60 * 60_000), // an hour ago
    }));
    TestBed.tick();

    expect(errorToasts(notify)).toHaveLength(0);
  });

  it('does NOT toast a failed run with unknown finish time on a fresh load', () => {
    const { statusSignal, notify } = setup();

    statusSignal.set(status({
      phase: 'failed',
      currentRunId: 'no-timestamp-run',
      message: 'restart failed (rc=1)',
      lastRunFinishedAt: null,
    }));
    TestBed.tick();

    expect(errorToasts(notify)).toHaveLength(0);
  });

  it('DOES toast a fresh, genuine failure during the session', () => {
    const { statusSignal, notify } = setup();

    statusSignal.set(status({
      phase: 'failed',
      currentRunId: 'fresh-run',
      message: 'restart failed (rc=1)',
      lastRunFinishedAt: isoAgo(2_000), // just now
    }));
    TestBed.tick();

    const toasts = errorToasts(notify);
    expect(toasts).toHaveLength(1);
    expect(toasts[0].title).toBe('Update failed');
  });

  it('persists a dismissed run so it stays dismissed after reload', () => {
    const fresh = isoAgo(2_000);

    // First load: fresh failure → toast shown, operator dismisses it.
    const first = setup();
    first.statusSignal.set(status({
      phase: 'failed',
      currentRunId: 'dismiss-me',
      message: 'restart failed (rc=1)',
      lastRunFinishedAt: fresh,
    }));
    TestBed.tick();

    const toast = errorToasts(first.notify)[0];
    expect(toast).toBeDefined();
    const dismissAction = toast.actions?.find(a => a.testId === 'toast-update-dismiss');
    expect(dismissAction).toBeDefined();
    dismissAction!.callback?.();

    // The run ID is now persisted.
    const persisted = JSON.parse(localStorage.getItem(DISMISSED_STORAGE_KEY) ?? '[]');
    expect(persisted).toContain('dismiss-me');

    // Simulate F5: brand-new bridge instance, same still-fresh failed status.
    TestBed.resetTestingModule();
    const second = setup();
    second.statusSignal.set(status({
      phase: 'failed',
      currentRunId: 'dismiss-me',
      message: 'restart failed (rc=1)',
      lastRunFinishedAt: fresh,
    }));
    TestBed.tick();

    // No toast: the dismissal survived the reload.
    expect(errorToasts(second.notify)).toHaveLength(0);
  });
});
