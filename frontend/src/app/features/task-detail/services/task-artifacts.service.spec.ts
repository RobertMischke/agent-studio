import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { TaskArtifactsService } from './task-artifacts.service';
import { TaskService } from '../../../services/task.service';
import type { TaskArtifact, TaskArtifactsResponse, TaskInfo } from '../../../models/task.model';

/**
 * The Files-tab count must reflect the CURRENT set of USER-RELEVANT `.md`
 * artifacts, not a value frozen when the task was first opened and not the
 * raw job-root listing (which includes orchestrator machinery). These tests
 * pin the poller contract that keeps the badge live — an immediate fetch on
 * `syncTo`, a forced re-fetch on `refresh`, a clear when no job is selected —
 * and the machinery filter that keeps the count honest (`orchestrator-follow-up.md`
 * must never reach the manifest, the root cause of the over-counted "9").
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

  it('drops orchestrator machinery so the Files count only sums user artifacts', () => {
    // The backend `/artifacts` listing includes `orchestrator-follow-up.md`
    // (the reissue reason written for the pickup runner). It sits in the job
    // root but is not a user "file"; it must not inflate the badge count.
    nextResponse = {
      jobId: 'a',
      files: [
        artifact('prompt.md'),
        artifact('aspect-code-quality.md'),
        artifact('orchestrator-follow-up.md'),
      ],
    };
    const svc = make();

    svc.syncTo(jobInfo('a'));

    expect(svc.artifacts().map((f) => f.name)).toEqual(['prompt.md', 'aspect-code-quality.md']);
  });

  it('matches machinery names case-insensitively', () => {
    nextResponse = {
      jobId: 'a',
      files: [artifact('prompt.md'), artifact('Orchestrator-Follow-Up.md')],
    };
    const svc = make();

    svc.syncTo(jobInfo('a'));

    expect(svc.artifacts().map((f) => f.name)).toEqual(['prompt.md']);
  });

  it('excludes every machinery reason file the backend writes into a folder root', () => {
    // failed-pickup / archive states drop their own reason file next to the
    // artifacts (see backend TaskAccessService). They are machinery, not
    // operator files, so they must not lift the count either.
    nextResponse = {
      jobId: 'a',
      files: [
        artifact('prompt.md'),
        artifact('failed-pickup-reason.md'),
        artifact('archive-reason.md'),
      ],
    };
    const svc = make();

    svc.syncTo(jobInfo('a'));

    expect(svc.artifacts().map((f) => f.name)).toEqual(['prompt.md']);
  });

  it('counts exactly the user-relevant artifacts named in the task spec', () => {
    // Spec: the badge should sum "nutzerrelevante Artefakte (prompt,
    // results/*, aspect-Dateien)" and code-review notes, while internal
    // machinery (logs/, run-context, lifecycle.json, pipeline-execution.json)
    // must NOT count. Subfolders + non-`.md` state never reach the frontend —
    // the backend `/artifacts` endpoint is top-level `*.md` + `aspect-*.json`
    // only (see TaskScannerService.ListArtifacts) — so a manifest that mixes
    // real artifacts with the one machinery `.md` that does leak through must
    // resolve to just the user-relevant set.
    nextResponse = {
      jobId: 'a',
      files: [
        artifact('prompt.md'),
        artifact('aspect-requirement-fit.md'),
        artifact('aspect-tests-and-evidence.md'),
        artifact('code-review-grade-2026-07-09.md'),
        artifact('orchestrator-follow-up.md'), // machinery — excluded
      ],
    };
    const svc = make();

    svc.syncTo(jobInfo('a'));

    const counted = svc.artifacts().map((f) => f.name);
    expect(counted).toEqual([
      'prompt.md',
      'aspect-requirement-fit.md',
      'aspect-tests-and-evidence.md',
      'code-review-grade-2026-07-09.md',
    ]);
    expect(counted).toHaveLength(4);
  });
});
