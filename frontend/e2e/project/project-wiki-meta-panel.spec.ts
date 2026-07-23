import { expect, test, type Page, type Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const PROJECT_NAME = 'Wiki Meta Panel Fixture';
const REPOSITORY_PATH = '/tmp/wiki-meta-panel-fixture';
const FIRST_PAGE = 'guide/one.md';
const SECOND_PAGE = 'guide/two.md';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function mockWiki(page: Page, projectName: string): Promise<void> {
  const project = encodeURIComponent(projectName);
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: projectName,
    path: REPOSITORY_PATH,
    rootPath: REPOSITORY_PATH,
    repositoryPath: REPOSITORY_PATH,
  }]));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: false,
    user: null,
  }));
  await page.route(`**/api/projects/${project}/wiki/tree`, route => json(route, {
    projectName,
    baseDir: '/repo/docs',
    exists: true,
    root: [{
      name: 'guide',
      title: 'Guide',
      relPath: 'guide',
      type: 'folder',
      children: [
        { name: 'one.md', title: 'One', relPath: FIRST_PAGE, type: 'md', children: [] },
        { name: 'two.md', title: 'Two', relPath: SECOND_PAGE, type: 'md', children: [] },
      ],
    }],
  }));
  await page.route(`**/api/projects/${project}/wiki/pulse**`, route => json(route, {
    projectName,
    baseDir: '/repo/docs',
    exists: true,
    generatedAtUtc: '2026-07-23T10:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Empty',
      areas: [],
      counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  }));
  await page.route(`**/api/projects/${project}/wiki/grading/status**`, route =>
    json(route, { status: null }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude',
    model: 'claude-sonnet-5',
    thinkingLevel: null,
  }));
  await page.route('**/api/tasks/reference-status', route => json(route, { items: [] }));
  await page.route(`**/api/projects/${project}/wiki/files/**`, route => {
    const relPath = decodeURIComponent(route.request().url().split('/wiki/files/')[1] ?? '');
    const content = relPath === FIRST_PAGE
      ? '# One\n\n[Second page](two.md)\n\n[AGT-2050](task:AGT-2050)'
      : '# Two\n\nSecond page body.';
    return json(route, { relPath, content });
  });
  await page.route(`**/api/projects/${project}/wiki/history/**`, route => {
    const relPath = decodeURIComponent(route.request().url().split('/wiki/history/')[1] ?? '');
    return json(route, {
      relPath,
      model: null,
      metadata: {
        model: null,
        updatedAt: null,
        reason: null,
        taskKey: null,
        status: null,
        runCount: null,
        hasFrontmatter: false,
      },
      commits: [],
    });
  });
}

test('meta-panel and section choices survive wiki navigation and reload', async ({ page }, testInfo) => {
  await mockWiki(page, PROJECT_NAME);

  await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/wiki?page=${encodeURIComponent(FIRST_PAGE)}`);
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(FIRST_PAGE);

  const metaToggle = page.getByTestId('project-wiki-meta-toggle');
  const linkedToggle = page.getByTestId('project-wiki-section-toggle-linked-elements');
  const historyToggle = page.getByTestId('project-wiki-section-toggle-history');
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'false');

  const linkedBeforeHistory = await page.evaluate(() => {
    const linked = document.querySelector('[data-testid="project-wiki-linked-elements"]');
    const history = document.querySelector('[data-testid="project-wiki-history-panel"]');
    return !!linked && !!history
      && (linked.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
  });
  expect(linkedBeforeHistory).toBe(true);

  const pageLink = page.getByTestId('project-wiki-linked-element').filter({ hasText: 'Second page' });
  await expect(pageLink).toHaveAttribute('title', 'Open wiki page: Second page');
  await expect(page.getByTestId('project-wiki-linked-element').filter({ hasText: 'AGT-2050' }))
    .toHaveAttribute('title', 'Open task AGT-2050');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: testInfo.outputPath(`wiki-meta-panel-${theme}.png`),
      fullPage: true,
    });
  }

  await pageLink.click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await page.getByTestId(`project-wiki-file-${FIRST_PAGE}`).click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(FIRST_PAGE);

  await linkedToggle.click();
  await historyToggle.click();
  await page.getByTestId(`project-wiki-file-${SECOND_PAGE}`).click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'true');

  await page.reload();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'true');
});
