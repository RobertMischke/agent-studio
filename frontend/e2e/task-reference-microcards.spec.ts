import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';

const results = process.env.JOB_RESULTS_DIR || path.resolve('test-results', 'task-reference-microcards');

const status = (key: string, title: string, lane: string, exists = true) => ({
  key, exists, taskKey: exists ? `PROJ-001::${key.toLowerCase()}` : null,
  title: exists ? title : null, lane: exists ? lane : null,
  projectId: 'PROJ-001', projectName: 'Agent Studio', projectColor: '#a78bfa',
  merge: exists ? { inIntegration: true, inRelease: false, integrationBranch: 'develop', releaseBranch: 'main' } : null,
  reviewGrade: exists ? 'A' : null,
});

test.beforeEach(async ({ page }) => {
  mkdirSync(results, { recursive: true });
  await page.route('**/api/**', route => route.fulfill({ contentType: 'application/json', body: '[]' }));
  await page.route('**/api/tasks/reference-status', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ items: [
      status('AGT-2050', 'Living task reference microcards', '3-progress'),
      status('AGT-2046', 'Always-on merge status', '6-completed'),
      status('AGT-9999', '', '', false),
    ] }),
  }));
  await page.goto('/');
  await page.locator('app-root').waitFor({ state: 'attached' });
});

test('Wiki task keys become compact reference controls in one batch', async ({ page }) => {
  const batches: string[][] = [];
  page.on('request', request => {
    if (request.url().includes('/api/tasks/reference-status')) batches.push(request.postDataJSON().keys);
  });
  await page.evaluate(() => {
    document.documentElement.dataset['studioTheme'] = 'light';
    document.body.innerHTML = `
      <main style="width:980px;margin:48px auto;padding:32px;background:var(--studio-bg-raised);border:1px solid var(--studio-border);border-radius:12px;color:var(--studio-fg);font:15px system-ui">
        <div style="color:var(--studio-fg-muted);font-size:12px;margin-bottom:12px">PROJECT WIKI / ENGINEERING / REFERENCES</div>
        <h1 style="margin:0 0 20px">Living task references</h1>
        <cac-markdown><p>The microcard work <b>AGT-2050</b> reuses merge semantics from AGT-2046 and preserves deleted AGT-9999.</p></cac-markdown>
        <section style="margin-top:28px;padding:20px;border-left:3px solid var(--studio-accent);background:var(--studio-bg)">
          References stay compact inside prose, carry status at a glance, and open the existing task tab.
        </section>
      </main>`;
  });
  await expect(page.getByTestId('task-reference-microcard')).toHaveCount(3);
  expect(batches).toHaveLength(1);
  await expect(page.locator('.task-ref__popover').first()).toContainText('Review grade A');
  await page.screenshot({ path: path.join(results, 'wiki-task-reference-microcards--mocked.png'), fullPage: true });
});

test('Chat uses the same host-provided reference control', async ({ page }) => {
  await page.evaluate(() => {
    delete document.documentElement.dataset['studioTheme'];
    document.body.innerHTML = `
      <main style="width:760px;margin:48px auto;color:var(--studio-fg);font:14px system-ui">
        <header style="padding:16px 20px;background:var(--studio-bg-raised);border:1px solid var(--studio-border);border-radius:12px 12px 0 0">ORCHESTRATOR CHAT · Agent Studio</header>
        <section style="padding:28px;background:var(--studio-bg);border:1px solid var(--studio-border);border-top:0">
          <article style="max-width:620px;padding:18px;background:var(--studio-bg-raised);border-radius:10px">
            <div style="color:var(--studio-fg-muted);font-size:12px;margin-bottom:10px">ORCHESTRATOR</div>
            <cac-markdown><p>AGT-2050 is in progress. Its merge indicator follows AGT-2046.</p></cac-markdown>
          </article>
        </section>
        <footer style="padding:16px 20px;border:1px solid var(--studio-border);border-top:0;border-radius:0 0 12px 12px;color:var(--studio-fg-muted)">Message the orchestrator...</footer>
      </main>`;
  });
  await expect(page.getByTestId('task-reference-microcard')).toHaveCount(2);
  await expect(page.locator('.task-ref__popover').first()).toContainText('develop: merged');
  await page.screenshot({ path: path.join(results, 'chat-task-reference-microcard--mocked.png'), fullPage: true });
});
