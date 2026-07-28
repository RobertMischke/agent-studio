import { describe, expect, it } from 'vitest';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import type { RemoteHost } from './remote-host.model';
import {
  boardRemoteSlotsForHost,
  deriveBoardRunningTruth,
  freshHostTelemetry,
  freshRemoteTelemetrySlots,
} from './running-truth';

function task(id: string, patch: Partial<TaskInfo>): TaskInfo {
  return {
    id,
    taskKey: id,
    title: id,
    state: TaskState.Progress,
    order: 0,
    agent: '',
    createdAt: '',
    watchPath: '/workspace',
    projectName: 'Project',
    folderPath: '',
    lastActivity: '',
    sessionName: null,
    model: null,
    cliType: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...patch,
  };
}

function host(activeSlots: number, timestamp: string): RemoteHost {
  return {
    id: 'runner-1',
    clientId: 'runner-1',
    name: 'runner-1',
    role: 'remote',
    address: null,
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: timestamp,
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: null,
    telemetry: {
      clientId: 'runner-1',
      window: '1h',
      findings: [],
      points: [{
        timestamp,
        cpuPercent: 20,
        load1: 1,
        load5: 1,
        load15: 1,
        memoryUsedBytes: 1,
        memoryTotalBytes: 2,
        swapInBytesPerSecond: 0,
        swapOutBytesPerSecond: 0,
        cpuStealPercent: 0,
        ioWaitPercent: 0,
        cpuCores: 4,
        activeSlots,
      }],
    },
  };
}

describe('running truth', () => {
  it('adds local executions and remote leased runs without double-counting', () => {
    const truth = deriveBoardRunningTruth([
      task('local', { execution: { status: 'running' } as TaskInfo['execution'] }),
      task('remote-a', {
        execution: { status: 'running' } as TaskInfo['execution'],
        runner: {
          runnerId: 'runner-1',
          runnerName: 'Runner 1',
          hostname: 'runner-1',
          backendName: 'task-server',
          isRemote: true,
          leaseId: 'lease-a',
          fencingToken: 1,
          acquiredAt: '2026-07-26T10:00:00Z',
        },
      }),
      task('remote-b', {
        runner: {
          runnerId: 'runner-1',
          runnerName: 'Runner 1',
          hostname: 'runner-1',
          backendName: 'task-server',
          isRemote: true,
          leaseId: 'lease-b',
          fencingToken: 2,
          acquiredAt: '2026-07-26T10:00:00Z',
        },
      }),
      task('orphan', {}),
    ]);

    expect(truth).toMatchObject({ local: 1, remote: 2, total: 3 });
    expect(boardRemoteSlotsForHost(truth, host(2, '2026-07-26T10:00:00Z'))).toBe(2);
  });

  it('rejects a stale telemetry sample even when the heartbeat is fresh', () => {
    const now = Date.parse('2026-07-26T10:10:00Z');
    const stale = host(3, '2026-07-26T10:00:00Z');
    stale.lastHeartbeatAt = '2026-07-26T10:09:30Z';

    expect(freshHostTelemetry(stale, now)).toBeNull();
    expect(freshRemoteTelemetrySlots([stale], now)).toBeNull();
  });

  it('counts a connected remote location but rejects its retained stale lease owner', () => {
    const activeLocation = {
      state: 'remote-running',
      executionKind: 'remote',
      runnerId: 'runner-1',
      hostDisplayName: 'Runner 1',
      startedAt: '2026-07-26T10:00:00Z',
      lastHeartbeat: '2026-07-26T10:01:00Z',
      lastActivityAt: '2026-07-26T10:01:00Z',
      connectionState: 'connected',
      leaseState: 'active',
      trustReason: 'Fresh lease heartbeat.',
    } as TaskInfo['executionLocation'];
    const runner = {
      runnerId: 'runner-1',
      runnerName: 'Runner 1',
      hostname: 'remote-host',
      backendName: 'remote',
      isRemote: true,
      leaseId: 'lease-1',
      fencingToken: 1,
      acquiredAt: '2026-07-26T10:00:00Z',
    };

    const truth = deriveBoardRunningTruth([
      task('fresh', { runner, executionLocation: activeLocation }),
      task('stale', {
        runner: { ...runner, leaseId: 'lease-2' },
        executionLocation: {
          ...activeLocation!,
          state: 'remote-disconnected',
          connectionState: 'disconnected',
          leaseState: 'expired',
        },
      }),
    ]);

    expect(truth).toMatchObject({ local: 0, remote: 1, total: 1 });
  });
});
