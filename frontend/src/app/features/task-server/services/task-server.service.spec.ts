import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TaskServerService } from './task-server.service';

const STATUS = {
  server: { id: 'ts-1', url: 'https://tasks.example', version: '1.0.0', protocolMinimum: '1.0', protocolMaximum: '1.0', uptimeSeconds: 60 },
  health: { state: 'healthy', ready: true },
  store: { sizeBytes: 10, projectCount: 1, taskCount: 2, archivedTaskCount: 3, eventCount: 4, artifactCount: 5, identityCount: 1 },
  evidence: { state: 'available', eventFiles: 2, artifactFiles: 1, lastWriteAt: null },
  maintenance: { mode: 'normal', drainRequested: false, shutdownPrepared: false, reason: null },
  migrations: [],
  runners: [{ id: 'runner-1', displayName: 'Runner 1', state: 'running', lastUsedAt: null, activeSlots: 0, drainRequested: false, retireRequested: false }],
  backups: { directory: '/backups', retentionCount: 7, lastFailure: null, items: [] },
  security: { available: true, userCount: 1, credentialRunnerCount: 1, sessionUrl: '/api/auth/session', usersUrl: '/api/auth/users', runnerCredentialsUrl: '/api/auth/runners', integration: 'shared' },
};

describe('TaskServerService', () => {
  let service: TaskServerService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(TaskServerService);
    http = TestBed.inject(HttpTestingController);
  });

  it('loads the authoritative management status', async () => {
    const pending = service.reload();
    http.expectOne('/api/v1/management/status').flush(STATUS);
    await pending;
    expect(service.status()?.connection.id).toBe('ts-1');
    expect(service.status()?.clients.map(x => x.id)).toEqual(['runner-1']);
  });

  it('previews a command and records its durable command id', async () => {
    const load = service.reload();
    http.expectOne('/api/v1/management/status').flush(STATUS);
    await load;
    const pending = service.runAction('archive-sweep');
    const request = http.expectOne('/api/v1/management/commands');
    expect(request.request.body.dryRun).toBe(true);
    request.flush({ commandId: 'cmd_1', kind: 'archive-sweep', dryRun: true, state: 'completed', matched: 2, affected: 0, summary: '2 would be archived.', completedAt: '2026-07-20T00:00:00Z' });
    await pending;
    expect(service.recentResults()[0].commandId).toBe('cmd_1');
    expect(service.recentResults()[0].dryRun).toBe(true);
  });
});
