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
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
