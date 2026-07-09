import { describe, expect, it, beforeEach, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { GitPaneService } from './git-pane.service';
import type { TaskInfo } from '../../../models/task.model';

/**
 * Regression: clicking a file in the git tree must always end up showing
 * THAT file's diff. Diff fetches are async, so a slow response for an
 * earlier-clicked file used to land after the user had already selected a
 * different file and overwrite `diffText` — leaving the wrong diff under the
 * new file's highlight + path label. `selectDiffPath` now pins each request
 * to the path it was issued for and drops stale results.
 */
describe('GitPaneService.selectDiffPath (out-of-order responses)', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  const job = {
    id: 'job-x',
    watchPath: '/wp',
  } as unknown as TaskInfo;

  const diffReq = (path: string) =>
    http.expectOne(
      (r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === path,
    );

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
    service.setJob(job);
    // setJob eagerly loads the landed-ladder provenance and the code-review
    // listing. Drain both so the diff assertions below start from a clean
    // HTTP queue and `http.verify()` doesn't trip over them.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({ entries: [] });
  });

  afterEach(() => http.verify());

  it('keeps the newly selected file when an earlier request resolves late', () => {
    // Click A — slow round-trip, left pending.
    service.selectDiffPath('a/x.ts');
    const reqA = diffReq('a/x.ts');

    // Click B before A resolves — B is now the selected file.
    service.selectDiffPath('b/y.ts');
    const reqB = diffReq('b/y.ts');
    expect(service.selectedDiffPath()).toBe('b/y.ts');

    // B resolves first and is shown.
    reqB.flush(utf8Buffer('DIFF_B'));
    expect(service.diffText()).toBe('DIFF_B');

    // A resolves late — it must NOT clobber the visible diff for B.
    reqA.flush(utf8Buffer('DIFF_A'));
    expect(service.selectedDiffPath()).toBe('b/y.ts');
    expect(service.diffText()).toBe('DIFF_B');
  });

  it('still caches the stale result so re-selecting that file is instant', () => {
    service.selectDiffPath('a/x.ts');
    const reqA = diffReq('a/x.ts');
    service.selectDiffPath('b/y.ts');
    const reqB = diffReq('b/y.ts');

    reqB.flush(utf8Buffer('DIFF_B'));
    reqA.flush(utf8Buffer('DIFF_A')); // stale for display, but cached.

    // Re-select A: served from cache, no second round-trip, correct text.
    service.selectDiffPath('a/x.ts');
    http.expectNone((r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === 'a/x.ts');
    expect(service.selectedDiffPath()).toBe('a/x.ts');
    expect(service.diffText()).toBe('DIFF_A');
  });
});

/**
 * Regression: the code-review listing feeds the commit-row rating badge via
 * the `commitReview` computed, which calls `codeReviews().find(...)`. The
 * listing contract is `{ entries: [...] }`, but a malformed body - notably a
 * bare `[]`, whose `.entries` is `Array.prototype.entries` (a truthy function)
 * - used to slip past `resp.entries ?? []` and land a function in the signal.
 * The next time a single commit was selected the computed threw
 * `find is not a function`, which aborted the git-pane's change-detection pass
 * and left the commit header half-rendered (empty file count, no message).
 * `loadCodeReviews` now trusts only an actual array.
 */
describe('GitPaneService.loadCodeReviews (defensive shape guard)', () => {
  let service: GitPaneService;
  let http: HttpTestingController;

  const commit = {
    sha: 'abcabcabcabcabcabcabcabcabcabcabcabcabca',
    shortSha: 'abcabca',
    message: 'only commit',
    filesChanged: 1,
    files: ['src/one.ts'],
    at: '2026-06-08T10:00:00Z',
  };
  const job = { id: 'job-x', watchPath: '/wp', commit } as unknown as TaskInfo;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        GitPaneService,
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(GitPaneService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('coerces a malformed (bare-array) listing body to an empty list so the badge computed never throws', () => {
    service.setJob(job);
    // A single-commit job pins the detail view to that commit, so `commitReview`
    // reaches its `codeReviews().find(...)` call rather than short-circuiting.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commit')).flush({ commit, files: [] });
    // The footgun: a bare array whose `.entries` is a function.
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush([]);

    expect(Array.isArray(service.codeReviews())).toBe(true);
    expect(service.codeReviews()).toEqual([]);
    // The rating-badge computed reads codeReviews().find(...); it must not throw.
    expect(() => service.commitReview()).not.toThrow();
    expect(service.commitReview()).toBeNull();
  });

  it('keeps a well-formed { entries } listing and matches the shown commit', () => {
    service.setJob(job);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/provenance')).flush(null);
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/commit')).flush({ commit, files: [] });
    http.expectOne((r) => r.url.endsWith('/api/tasks/job-x/code-review/list')).flush({
      entries: [
        { fileName: 'r.md', verdict: 'pass', summary: 'ok', model: 'm', cliType: 'claude', commit: commit.sha, runAt: '2026-06-08T11:00:00Z' },
      ],
    });

    expect(service.codeReviews()).toHaveLength(1);
    expect(service.commitReview()?.verdict).toBe('pass');
  });
});

function utf8Buffer(value: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(value);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
