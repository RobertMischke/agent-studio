import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { TaskArtifactsService } from './task-artifacts.service';
import { TaskService } from '../../../services/task.service';
import type { TaskArtifact, TaskArtifactsResponse, TaskInfo } from '../../../models/task.model';

/**
 * The Files-tab count must reflect the CURRENT set of `.md` artifacts, not
 * a value frozen when the task was first opened. These tests pin the poller
 * contract that keeps the badge live: an immediate fetch on `syncTo`, a
 * forced re-fetch on `refresh`, and a clear when no job is selected.
 */
describe('TaskArtifactsService', () => {
  let calls: Array<{ jobId: string; watchPath?: string }>;
  let nextResponse: TaskArtifactsResponse | null;

  function artifact(name: string): TaskArtifact {
    return { name, sizeBytes: 1, mtime: '2026-07-09T00:00:00Z', kind: 'other' };
  }

  function jobInfo(id: string): TaskInfo {
    return { id, watchPath: '/wp', taskKey: `wp::${id}` } as unknown as TaskInfo;
  }

  const taskServiceStub = {
    listJobArtifacts(jobId: string, watchPath?: string) {
      calls.push({ jobId, watchPath });
      // The runtime tolerates a null body (empty manifest); mirror that
      // here even though the declared type is non-null.
      return of(nextResponse) as unknown as ReturnType<TaskService['listJobArtifacts']>;
    },
  };

  function make(): TaskArtifactsService {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        TaskArtifactsService,
        { provide: TaskService, useValue: taskServiceStub as unknown as TaskService },
      ],
    });
    return TestBed.inject(TaskArtifactsService);
  }

  beforeEach(() => {
    calls = [];
    nextResponse = null;
    TestBed.resetTestingModule();
  });

  it('fetches and publishes the artifact list immediately on syncTo', () => {
    nextResponse = { jobId: 'a', files: [artifact('prompt.md'), artifact('aspect-x.md')] };
    const svc = make();

    svc.syncTo(jobInfo('a'));

    expect(calls).toHaveLength(1);
    expect(svc.artifacts().map((f) => f.name)).toEqual(['prompt.md', 'aspect-x.md']);
  });

  it('re-fetches on refresh so a newly generated file lifts the count live', () => {
    nextResponse = { jobId: 'a', files: [artifact('prompt.md')] };
    const svc = make();
    svc.syncTo(jobInfo('a'));
    expect(svc.artifacts()).toHaveLength(1);

    // A run drops a fresh aspect file; the next poll/refresh must pick it up.
    nextResponse = { jobId: 'a', files: [artifact('prompt.md'), artifact('aspect-y.md')] };
    svc.refresh();

    expect(calls).toHaveLength(2);
    expect(svc.artifacts()).toHaveLength(2);
  });

  it('clears the list when no job is selected', () => {
    nextResponse = { jobId: 'a', files: [artifact('prompt.md'), artifact('aspect-x.md')] };
    const svc = make();
    svc.syncTo(jobInfo('a'));
    expect(svc.artifacts()).toHaveLength(2);

    svc.syncTo(null);
    expect(svc.artifacts()).toEqual([]);
  });

  it('treats a null response as an empty manifest rather than throwing', () => {
    nextResponse = null;
    const svc = make();
    svc.syncTo(jobInfo('a'));
    expect(svc.artifacts()).toEqual([]);
  });
});
