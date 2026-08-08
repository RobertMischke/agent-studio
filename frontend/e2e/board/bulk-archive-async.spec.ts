import { expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { test } from '../fixtures/dev-backend';

const CARD_COUNT = 55;
const CLIENT_ID = 'local-default';

interface WatchPath {
  path: string;
}

interface CreatedTask {
  id: string;
}

interface TaskRow {
  id: string;
  state: string;
}

async function request<T>(baseUrl: string, route: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${baseUrl}${route}`, {
    ...init,
    headers: {
      'content-type': 'application/json',
      'x-client-id': CLIENT_ID,
      ...(init.headers ?? {}),
    },
  });
  const body = await response.text();
  if (!response.ok) throw new Error(`${init.method ?? 'GET'} ${route} -> ${response.status}: ${body}`);
  return body ? JSON.parse(body) as T : undefined as T;
}

function resultPath(name: string): string {
  const results = process.env.JOB_RESULTS_DIR;
  if (!results) throw new Error('JOB_RESULTS_DIR is required for bulk archive evidence.');
  mkdirSync(results, { recursive: true });
  return path.join(results, name);
}

test.describe('Bulk archive responsiveness', () => {
  test('measures the 55-card UI path and concurrent Review access', async ({ page, devBackend }) => {
    test.setTimeout(180_000);
    const watchPaths = await request<WatchPath[]>(devBackend.baseUrl, '/api/watch-paths');
    if (!watchPaths.length) throw new Error('No watch path configured.');
    const watchPath = watchPaths[0].path;
    const runId = `bulk-archive-${Date.now()}`;
    const created: string[] = [];

    try {
      const review = await request<CreatedTask>(devBackend.baseUrl, '/api/tasks', {
        method: 'POST',
        body: JSON.stringify({
          id: `${runId}-review`,
          title: `${runId} review access sentinel`,
          watchPath,
          targetState: '5-human-review',
          fixture: false,
        }),
      });
      created.push(review.id);

      const tasks = await Promise.all(Array.from({ length: CARD_COUNT }, async (_, index) => {
        const task = await request<CreatedTask>(devBackend.baseUrl, '/api/tasks', {
          method: 'POST',
          body: JSON.stringify({
            id: `${runId}-${String(index + 1).padStart(2, '0')}`,
            title: `${runId} completed ${String(index + 1).padStart(2, '0')}`,
            watchPath,
            targetState: '6-completed',
            fixture: false,
          }),
        });
        created.push(task.id);
        return task;
      }));

      await page.goto('/');
      const completedLane = page.getByTestId('lane-6-completed');
      await expect.poll(
        () => completedLane.getByTestId('task-card').count(),
        { timeout: 30_000, intervals: [200, 500, 1_000] },
      ).toBeGreaterThanOrEqual(CARD_COUNT);

      const button = page.getByTestId('archive-all-btn');
      const archiveStartedAt = performance.now();
      await button.click();

      const reviewStartedAt = performance.now();
      const reviewResponse = await fetch(
        `${devBackend.baseUrl}/api/tasks/${encodeURIComponent(review.id)}/code-review/list?watchPath=${encodeURIComponent(watchPath)}`,
        { headers: { 'x-client-id': CLIENT_ID } },
      );
      const reviewDurationMs = performance.now() - reviewStartedAt;
      expect(reviewResponse.ok).toBe(true);

      await expect.poll(async () => {
        const rows = await request<TaskRow[]>(devBackend.baseUrl, '/api/tasks?includeFixtures=true');
        return tasks.filter(task => rows.some(row => row.id === task.id && row.state === '7-archive')).length;
      }, { timeout: 120_000, intervals: [200, 500, 1_000] }).toBe(CARD_COUNT);

      const archiveDurationMs = performance.now() - archiveStartedAt;
      writeFileSync(resultPath('bulk-archive-before.json'), JSON.stringify({
        measuredAtUtc: new Date().toISOString(),
        implementation: 'legacy-ui-parallel-single-move-requests',
        cardCount: CARD_COUNT,
        archiveDurationMs: Math.round(archiveDurationMs * 10) / 10,
        concurrentReviewDurationMs: Math.round(reviewDurationMs * 10) / 10,
        reviewStatus: reviewResponse.status,
      }, null, 2));
    } finally {
      await Promise.all(created.map(id => request<void>(
        devBackend.baseUrl,
        `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      ).catch(() => undefined)));
    }
  });
});
