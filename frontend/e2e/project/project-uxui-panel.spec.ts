import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Slice 6 of the quality-system mockup (docs/mockups/quality-system/):
 * the project UX/UI panel, mounted under the project shell's UX/UI rail
 * item. The spec covers the four contracts the prompt names explicitly:
 *
 * 1. Empty state: no design folder yet -> the metric grid renders zeros
 *    and the references / council-empty placeholders are visible. All
 *    four action buttons are present.
 * 2. Populated state: brief.md + a few references + a council note land
 *    the counts, drive the references-grid card kinds, and surface the
 *    council row with its category chip.
 * 3. parseOk = false fallback: a malformed council note renders the
 *    "unstructured report" warning while still listing the file (Report
 *    Contracts in the README).
 * 4. Action wiring: clicking each of the four action buttons either
 *    queues a design-loop CLI job (chip turns into "Action queued ...")
 *    or opens the create-job dialog (Create follow-up task). Council Accept
 *    stamps acceptedAt into the note's frontmatter.
 *
 * Fixtures land directly under the watched project's design/ folder; the
 * backend reads them on every poll so we don't have to invalidate any cache.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_UXUI_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-uxui-panel');
})();

let projectName = '';
let projectPath = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  projectPath = preferred.path;
});

test.beforeEach(async () => {
  // Each test owns the project's design/ subtree so the empty-state run
  // and the populated run don't leak fixtures into each other.
  const dir = path.join(projectPath, 'design');
  if (fs.existsSync(dir)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test.afterAll(async () => {
  const dir = path.join(projectPath, 'design');
  if (fs.existsSync(dir)) {
    try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* best-effort */ }
  }
  await cleanupQueuedDesignJobs();
});

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function plant(rel: string, content: string): void {
  const full = path.join(projectPath, 'design', rel);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, content, 'utf8');
}

async function openUxuiRail(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('project-shell-rail-uxui').click();
  await expect(page.getByTestId('uxui-panel')).toBeVisible();
  expect(page.url()).toContain(`#/projects/${slugFor(projectName)}/uxui`);
}

test('empty state - no design folder, all action buttons render', async ({ page }) => {
  await openUxuiRail(page);

  await expect(page.getByTestId('uxui-card-references-value')).toContainText('0');
  await expect(page.getByTestId('uxui-card-screenshots-value')).toContainText('0');
  await expect(page.getByTestId('uxui-card-council-value')).toContainText('0');
  await expect(page.getByTestId('uxui-council-empty')).toBeVisible();

  // All four action buttons are present.
  await expect(page.getByTestId('uxui-run-screenshot-critique')).toBeVisible();
  await expect(page.getByTestId('uxui-run-council-review')).toBeVisible();
  await expect(page.getByTestId('uxui-request-next-version')).toBeVisible();
  await expect(page.getByTestId('uxui-create-followup')).toBeVisible();
  await expect(page.getByTestId('uxui-add-reference')).toBeVisible();

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/01-empty-state.png`,
    fullPage: true,
  });
});

test('populated state - brief, references, and council note populate the panel', async ({ page }) => {
  plant('brief.md',
    '---\n' +
    'status: iteration-active\n' +
    'lastUpdated: 2026-04-15\n' +
    'summary: Workbench-first dark UI; deny marketing-page polish.\n' +
    '---\n\n' +
    '## Tone\n\nDense, technical, evidence-first.\n');
  plant('references/workbench-shell.md',
    '---\n' +
    'kind: accepted\n' +
    'title: Workbench shell\n' +
    'summary: Final layout that landed in slice 2.\n' +
    '---\n');
  plant('references/marketing-hero.md',
    '---\n' +
    'kind: rejected\n' +
    'title: Marketing-style hero\n' +
    'summary: Tried but felt off-brand.\n' +
    '---\n');
  plant('references/vscode-density.md',
    '---\n' +
    'kind: external\n' +
    'title: VS Code density reference\n' +
    'summary: External screenshot used as inspiration.\n' +
    '---\n');
  plant('council/2026-04-12-product.md',
    '---\n' +
    'date: 2026-04-12\n' +
    'category: workflow\n' +
    'title: Product\n' +
    'summary: First viewport now shows project state, but QA paths need clearer intent.\n' +
    '---\n\n' +
    'Body of the council note.\n');

  await openUxuiRail(page);

  await expect(page.getByTestId('uxui-card-references-value')).toContainText('3');
  await expect(page.getByTestId('uxui-card-screenshots-value')).toContainText('2');
  await expect(page.getByTestId('uxui-screenshots-accepted')).toContainText('1');
  await expect(page.getByTestId('uxui-screenshots-rejected')).toContainText('1');
  await expect(page.getByTestId('uxui-card-council-value')).toContainText('1');

  // Reference grid populated.
  await expect(page.getByTestId('uxui-ref-accepted')).toContainText('Workbench shell');
  await expect(page.getByTestId('uxui-ref-rejected')).toContainText('Marketing-style hero');
  await expect(page.getByTestId('uxui-ref-external')).toContainText('VS Code density reference');

  // Council row visible with category tag.
  const rows = page.locator('[data-testid="uxui-council-row"]');
  await expect(rows).toHaveCount(1);
  await expect(rows.nth(0)).toContainText('Product');
  await expect(rows.nth(0)).toContainText('workflow');

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/02-populated.png`,
    fullPage: true,
  });
});

test('parseOk = false council note renders unstructured-report warning with raw markdown', async ({ page }) => {
  plant('council/2026-04-30-prose-only.md',
    '# Free-form note\n\n' +
    'The agent forgot to emit a structured block. The panel must show the raw ' +
    'Markdown with an unstructured-report warning instead of pretending to know ' +
    'the category.\n');

  await openUxuiRail(page);

  const row = page.locator('[data-testid="uxui-council-row"][data-parse-ok="false"]');
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('uxui-unstructured-warning')).toBeVisible();

  // Raw markdown is loaded on demand.
  await row.getByTestId('uxui-council-load-raw').click();
  await expect(row.getByTestId('uxui-council-raw')).toContainText('Free-form note');

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/03-unstructured.png`,
    fullPage: true,
  });
});

test('Run screenshot critique queues a design-loop job and surfaces the chip', async ({ page }) => {
  await openUxuiRail(page);

  await page.getByTestId('uxui-run-screenshot-critique').click();
  await expect(page.getByTestId('uxui-action-queued')).toBeVisible({ timeout: 10_000 });
  const okText = await page.getByTestId('uxui-action-queued').textContent();
  expect(okText ?? '').toMatch(/queued/i);

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/04-action-queued.png`,
    fullPage: true,
  });

  await cleanupQueuedDesignJobs();
});

test('Run council review queues a design-loop job', async ({ page }) => {
  await openUxuiRail(page);
  await page.getByTestId('uxui-run-council-review').click();
  await expect(page.getByTestId('uxui-action-queued')).toBeVisible({ timeout: 10_000 });
  await cleanupQueuedDesignJobs();
});

test('Request next version queues a design-loop job', async ({ page }) => {
  await openUxuiRail(page);
  await page.getByTestId('uxui-request-next-version').click();
  await expect(page.getByTestId('uxui-action-queued')).toBeVisible({ timeout: 10_000 });
  await cleanupQueuedDesignJobs();
});

test('Create follow-up task opens the create-job dialog with prefilled prompt', async ({ page }) => {
  plant('council/2026-04-12-product.md',
    '---\n' +
    'date: 2026-04-12\n' +
    'category: workflow\n' +
    'title: Product\n' +
    'summary: First viewport now shows project state.\n' +
    '---\n');

  await openUxuiRail(page);
  await page.getByTestId('uxui-create-followup').click();

  await expect(page.getByTestId('create-dialog-close')).toBeVisible({ timeout: 10_000 });
  const titleInput = page.getByPlaceholder('Task title');
  await expect(titleInput).toHaveValue(/Design follow-up/i);
  const promptArea = page.getByTestId('create-prompt');
  const promptValue = (await promptArea.inputValue()) ?? '';
  expect(promptValue).toMatch(/design follow-up/i);

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/05-create-followup.png`,
    fullPage: true,
  });
});

test('Council Accept stamps acceptedAt into the note', async ({ page }) => {
  plant('council/2026-04-12-product.md',
    '---\n' +
    'date: 2026-04-12\n' +
    'category: workflow\n' +
    'title: Product\n' +
    'summary: Open finding to accept.\n' +
    '---\n');

  await openUxuiRail(page);
  const row = page.locator('[data-testid="uxui-council-row"]').first();
  await expect(row).toBeVisible();
  await row.getByTestId('uxui-council-accept').click();

  // After accept, the row gets the accepted attribute (panel re-fetches).
  await expect(page.locator('[data-testid="uxui-council-row"][data-accepted="true"]'))
    .toHaveCount(1, { timeout: 10_000 });

  // File on disk now contains acceptedAt.
  const updated = fs.readFileSync(path.join(projectPath, 'design', 'council', '2026-04-12-product.md'), 'utf8');
  expect(updated).toMatch(/acceptedAt:\s*\d{4}-\d{2}-\d{2}T/);

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/06-accepted.png`,
    fullPage: true,
  });
});

void BACKEND;

async function cleanupQueuedDesignJobs(): Promise<void> {
  type Job = { id: string; title?: string | null; state?: string; watchPath?: string };
  try {
    const list = await api<Job[]>(`/api/jobs`);
    const designJobs = list.filter(j =>
      (j.title ?? '').toLowerCase().startsWith('design:') &&
      (j.watchPath?.toLowerCase() === projectPath.toLowerCase()),
    );
    for (const j of designJobs) {
      try {
        await api(`/api/jobs/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(projectPath)}`, { method: 'DELETE' });
      } catch { /* best-effort cleanup */ }
    }
  } catch { /* best-effort */ }
}
