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
    reqB.flush('DIFF_B');
    expect(service.diffText()).toBe('DIFF_B');

    // A resolves late — it must NOT clobber the visible diff for B.
    reqA.flush('DIFF_A');
    expect(service.selectedDiffPath()).toBe('b/y.ts');
    expect(service.diffText()).toBe('DIFF_B');
  });

  it('still caches the stale result so re-selecting that file is instant', () => {
    service.selectDiffPath('a/x.ts');
    const reqA = diffReq('a/x.ts');
    service.selectDiffPath('b/y.ts');
    const reqB = diffReq('b/y.ts');

    reqB.flush('DIFF_B');
    reqA.flush('DIFF_A'); // stale for display, but cached.

    // Re-select A: served from cache, no second round-trip, correct text.
    service.selectDiffPath('a/x.ts');
    http.expectNone((r) => r.url.endsWith('/api/tasks/job-x/git/diff') && r.params.get('path') === 'a/x.ts');
    expect(service.selectedDiffPath()).toBe('a/x.ts');
    expect(service.diffText()).toBe('DIFF_A');
  });
});
