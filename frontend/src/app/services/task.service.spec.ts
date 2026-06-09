import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TaskService } from './task.service';
import { ErrorDialogService } from './error-dialog.service';
import { JobsHubClient } from './jobs-hub-client.service';

class ErrorDialogServiceStub {
  show(): void {}
}

class JobsHubClientStub {
  readonly connected = signal(false);
  start(): void {}
  stop(): void {}
}

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
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
