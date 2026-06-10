import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TaskService } from './task.service';
import { ErrorDialogService } from './error-dialog.service';
import { JobsHubClient, type JobsHubHandlers } from './jobs-hub-client.service';

class ErrorDialogServiceStub {
  show(): void { return undefined; }
}

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void {
    this.handlers = handlers;
  }
  stop(): void { return undefined; }
}

const emptyGrouped = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

describe('TaskService', () => {
  let service: TaskService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ErrorDialogService, useClass: ErrorDialogServiceStub },
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    });

    service = TestBed.inject(TaskService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('decodes job file content from UTF-8 bytes', () => {
    const expected = 'Lücken / gehört / für / „Anführung"';
    let actual = '';

    service.readJobFile('demo-job', 'prompt.md', 'C:/projects/demo').subscribe((text) => {
      actual = text;
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/prompt.md' &&
      request.params.get('watchPath') === 'C:/projects/demo');
    expect(req.request.responseType).toBe('arraybuffer');

    req.flush(utf8Buffer(expected), {
      headers: { 'Content-Type': 'text/plain' },
    });

    expect(actual).toBe(expected);
  });

  it('decodes raw diff endpoints from UTF-8 bytes', () => {
    const expected = '+überarbeitete Zeile';
    let actual = '';

    service.getGitDiff('demo-job', 'src/file.ts', 'C:/projects/demo').subscribe((text) => {
      actual = text;
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/git/diff' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('path') === 'src/file.ts');
    expect(req.request.responseType).toBe('arraybuffer');

    req.flush(utf8Buffer(expected), {
      headers: { 'Content-Type': 'text/plain' },
    });

    expect(actual).toBe(expected);
  });

  it('rehydrates runner status through the SignalR reconnect hook', () => {
    const hub = TestBed.inject(JobsHubClient) as unknown as JobsHubClientStub;

    service.startLiveUpdates();
    http.expectOne('/api/projects/settings').flush({});

    expect(hub.handlers?.reconnected).toBeTruthy();
    hub.handlers?.reconnected?.();

    http.expectOne('/api/tasks').flush([]);
    http.expectOne('/api/tasks/grouped').flush(emptyGrouped);
    http.expectOne('/api/runner/status').flush({
      projects: {
        demo: {
          projectName: 'demo',
          mode: 'auto-continuous',
          activeJobId: 'job-1',
          activeExecution: null,
          queuedJobIds: ['job-2'],
        },
      },
    });

    expect(service.runnerStatus().projects['demo'].activeJobId).toBe('job-1');
    expect(service.runnerStatus().projects['demo'].mode).toBe('auto-continuous');
    service.stopLiveUpdates();
  });

  it('calls the file-source history endpoints with encoded paths and source params', () => {
    let historyCount = 0;
    service
      .getTaskFileHistory('demo-job', 'results/review note.md', 'C:/projects/demo', 'workspace')
      .subscribe((entries) => {
        historyCount = entries.length;
      });

    const historyReq = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/results/review%20note.md/history' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('scope') === 'workspace');
    historyReq.flush([{ sha: 'abcdef1', at: '2026-06-09T12:00:00Z', message: 'capture', author: 'A', provenance: { source: 'workspace', path: 'results/review note.md' } }]);
    expect(historyCount).toBe(1);

    let version = '';
    service
      .readTaskFileAt('demo-job', 'results/review note.md', 'abcdef1', 'C:/projects/demo', 'workspace')
      .subscribe((text) => {
        version = text;
      });

    const versionReq = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/results/review%20note.md' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('scope') === 'workspace' &&
      request.params.get('at') === 'abcdef1');
    expect(versionReq.request.responseType).toBe('arraybuffer');
    versionReq.flush(utf8Buffer('# Old review\n'));
    expect(version).toBe('# Old review\n');

    let diff = '';
    service
      .diffTaskFileVersions('demo-job', 'results/review note.md', 'abcdef1', 'abcdef2', 'C:/projects/demo', 'workspace')
      .subscribe((text) => {
        diff = text;
      });

    const diffReq = http.expectOne((request) =>
      request.url === '/api/tasks/demo-job/files/results/review%20note.md/diff' &&
      request.params.get('watchPath') === 'C:/projects/demo' &&
      request.params.get('scope') === 'workspace' &&
      request.params.get('from') === 'abcdef1' &&
      request.params.get('to') === 'abcdef2');
    expect(diffReq.request.responseType).toBe('arraybuffer');
    diffReq.flush(utf8Buffer('-old\n+new\n'));
    expect(diff).toBe('-old\n+new\n');
  });

  // ASS-1727: the paged Archive read endpoint. The board's grouped.archive is
  // intentionally empty, so the Archive view reads here. Verify the query is
  // built correctly (offset/limit/trimmed search) and the envelope passes
  // through untouched.
  it('pages the archive endpoint with offset/limit and a trimmed search term', () => {
    let total = -1;
    let ids: string[] = [];
    service.getArchivedTasks({ offset: 50, limit: 25, search: '  migration  ' }).subscribe((res) => {
      total = res.total;
      ids = res.items.map((i) => i.id);
    });

    const req = http.expectOne((request) =>
      request.url === '/api/tasks/archive' &&
      request.params.get('offset') === '50' &&
      request.params.get('limit') === '25' &&
      request.params.get('search') === 'migration');
    req.flush({
      items: [{ id: 'arch-1', taskKey: 't::arch-1', title: 'Archived', state: '7-archive' }],
      total: 873,
      offset: 50,
      limit: 25,
    });

    expect(total).toBe(873);
    expect(ids).toEqual(['arch-1']);
  });

  it('omits empty optional params from the archive query', () => {
    service.getArchivedTasks().subscribe();

    const req = http.expectOne((request) => request.url === '/api/tasks/archive');
    expect(req.request.params.has('offset')).toBe(false);
    expect(req.request.params.has('limit')).toBe(false);
    expect(req.request.params.has('search')).toBe(false);
    expect(req.request.params.has('watchPath')).toBe(false);
    req.flush({ items: [], total: 0, offset: 0, limit: 50 });
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
