import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

interface WatchPath { name: string }

const REL_PATH = 'design/tree-indicator-exploration-2026-07.html';
const ARTIFACT_PATH = path.resolve(__dirname, '..', '..', '..', 'docs', REL_PATH);
const SCREENSHOT_DIR = process.env.JOB_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots');

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test('AGT-2083 exploration runs scripts while parent access stays blocked', async ({ page }) => {
  const projects = await api<WatchPath[]>('/api/watch-paths');
  expect(projects.length).toBeGreaterThan(0);
  const projectName = projects[0].name;
  const encodedProject = encodeURIComponent(projectName);
  const artifactHtml = fs.readFileSync(ARTIFACT_PATH, 'utf8');

  await page.route(`**/api/projects/${encodedProject}/wiki/tree`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projectName,
      baseDir: '/repo/docs',
      exists: true,
      root: [{
        name: 'design', title: 'design', relPath: 'design', type: 'folder',
        children: [{
          name: 'tree-indicator-exploration-2026-07.html',
          title: 'Explorer project state indicator exploration',
          relPath: REL_PATH,
          type: 'html',
          children: [],
        }],
      }],
    }),
  }));
  await page.route(`**/api/projects/${encodedProject}/wiki/pulse**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projectName, baseDir: '/repo/docs', exists: true, generatedAtUtc: new Date().toISOString(),
      feed: { available: true, reason: null, items: [] },
      inbox: { available: true, reason: null, count: 0, items: [] },
      drift: { available: true, reason: null, overallGrade: 'Empty', areas: [],
        counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
      critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
    }),
  }));
  await page.route(`**/api/projects/${encodedProject}/wiki/grading/status**`, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ status: null }),
  }));
  await page.route('**/api/cli/maintenance-model', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null }),
  }));
  await page.route(`**/api/projects/${encodedProject}/wiki/files/${REL_PATH}`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ relPath: REL_PATH, content: artifactHtml }),
  }));
  await page.route(`**/api/projects/${encodedProject}/wiki/history/${REL_PATH}`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      relPath: REL_PATH, model: null,
      metadata: { model: null, updatedAt: null, reason: null, taskKey: 'AGT-2083',
        status: null, runCount: null, hasFrontmatter: false },
      commits: [],
    }),
  }));

  await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
  await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('project-wiki-filter-toggle').click();
  await page.getByTestId('project-wiki-filter').fill('tree-indicator-exploration-2026-07');
  await page.getByTestId(`project-wiki-file-${REL_PATH}`).click();

  const frameElement = page.getByTestId('project-wiki-html-frame');
  await expect(frameElement).toBeVisible({ timeout: 10_000 });
  await expect(frameElement).toHaveAttribute('sandbox', 'allow-scripts');
  const exploration = page.frameLocator('[data-testid="project-wiki-html-frame"]');
  await expect(exploration.locator('body')).toHaveAttribute('data-option', 'dots');
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, 'agt-2083-option-a-interactive-isolated.png'),
    fullPage: true,
  });
  await exploration.locator('[data-option="pulse"]').click();
  await expect(exploration.locator('body')).toHaveAttribute('data-option', 'pulse');
  await expect(exploration.locator('#option-title')).toHaveText('E. Pulse + dashboard');
  await exploration.locator('body').evaluate(() => window.scrollTo(0, 0));
  await expect(exploration.locator('#option-title')).toBeVisible();

  const handle = await frameElement.elementHandle();
  const childFrame = await handle?.contentFrame();
  expect(childFrame, 'AGT-2083 iframe content frame').toBeTruthy();
  expect(await childFrame!.evaluate(() => {
    try {
      void window.parent.document.body;
      return 'allowed';
    } catch {
      return 'blocked';
    }
  })).toBe('blocked');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, 'agt-2083-option-e-interactive-isolated.png'),
    fullPage: true,
  });
});
