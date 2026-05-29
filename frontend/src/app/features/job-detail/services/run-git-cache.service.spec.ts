import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunGitCacheService } from './run-git-cache.service';
import type { RunDiffResponse, RunFilesResponse } from '../../run-timeline';

/**
 * Covers the per-run files/diff cache that closes the performance gap
 * the operator flagged ("switching between runs and diffs is slow").
 *
 *   1. A repeated `getFiles` for the same (jobId, runIndex, watchPath)
 *      hits the cache and fires zero additional HTTP calls.
 *   2. `getDiff` keys on the path; sibling paths still fan out.
 *   3. `invalidate(jobId, watchPath)` drops every entry for one job
 *      without touching siblings.
 */
describe('RunGitCacheService', () => {
  let service: RunGitCacheService;
  let http: HttpTestingController;

  const filesFixture: RunFilesResponse = {
    runIndex: 1,
    headShaBefore: 'aaa',
    headShaAfter: 'bbb',
    files: [{ status: 'M', path: 'src/x.ts', added: 3, removed: 1 }],
  } as unknown as RunFilesResponse;

  const diffFixture: RunDiffResponse = { diff: '--- a\n+++ b\n' } as RunDiffResponse;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    service = TestBed.inject(RunGitCacheService);
    http = TestBed.inject(HttpTestingController);
    service.clear();
  });

  it('serves a second getFiles from the cache (zero extra requests)', () => {
    service.getFiles('job-a', 1, '/wp').subscribe();
    const req = http.expectOne(r => r.url.endsWith('/api/jobs/job-a/runs/1/files'));
    req.flush(filesFixture);

    let payload: RunFilesResponse | null = null;
    service.getFiles('job-a', 1, '/wp').subscribe(r => (payload = r));
    http.expectNone(r => r.url.endsWith('/api/jobs/job-a/runs/1/files'));
    expect(payload).not.toBeNull();
    expect(payload!.files[0].path).toBe('src/x.ts');
  });

  it('keys diffs by path so different files still round-trip', () => {
    service.getDiff('job-b', 1, 'src/a.ts', '/wp').subscribe();
    http.expectOne(r => r.url.includes('/api/jobs/job-b/runs/1/diff') && r.params.get('path') === 'src/a.ts')
      .flush(diffFixture);

    service.getDiff('job-b', 1, 'src/b.ts', '/wp').subscribe();
    // Sibling path is uncached and must round-trip.
    http.expectOne(r => r.url.includes('/api/jobs/job-b/runs/1/diff') && r.params.get('path') === 'src/b.ts')
      .flush(diffFixture);

    // Re-asking for the original path is a cache hit.
    service.getDiff('job-b', 1, 'src/a.ts', '/wp').subscribe();
    http.expectNone(r => r.url.includes('/api/jobs/job-b/runs/1/diff') && r.params.get('path') === 'src/a.ts');
  });

  it('invalidate drops every entry for one job, leaves siblings alone', () => {
    service.getFiles('job-c', 1, '/wp').subscribe();
    http.expectOne(r => r.url.endsWith('/api/jobs/job-c/runs/1/files')).flush(filesFixture);
    service.getFiles('job-d', 1, '/wp').subscribe();
    http.expectOne(r => r.url.endsWith('/api/jobs/job-d/runs/1/files')).flush(filesFixture);

    service.invalidate('job-c', '/wp');

    // After invalidate, job-c must re-fetch; job-d still hits the cache.
    service.getFiles('job-c', 1, '/wp').subscribe();
    http.expectOne(r => r.url.endsWith('/api/jobs/job-c/runs/1/files')).flush(filesFixture);
    service.getFiles('job-d', 1, '/wp').subscribe();
    http.expectNone(r => r.url.endsWith('/api/jobs/job-d/runs/1/files'));
  });
});
