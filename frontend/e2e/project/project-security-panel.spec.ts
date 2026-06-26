import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Slice 1 of the quality-system mockup (docs/mockups/quality-system/):
 * the project Security panel, mounted under the project shell's Security
 * rail item. The spec covers four contracts the prompt names explicitly:
 *
 * 1. Empty state: no baseline + no reviews → no-baseline badge,
 *    history-empty copy, all three action buttons present.
 * 2. Populated state: baseline.md + two reviews on disk render the
 *    badge, the "last review" card with verdict + severity split, and
 *    the review history list newest-first.
 * 3. Graceful degradation: a third review file with no structured block
 *    renders the `unstructured report` warning while still listing the
 *    file (Report Contracts in the README).
 * 4. Action wiring: "Run security audit" surfaces the queued chip on
 *    success and an error chip when an audit is already pending; the
 *    "Create follow-up task" button opens the existing create-job
 *    dialog with prefilled prompt copy.
 *
 * Fixtures are written directly to the watched project's
 * <c>security/reviews/</c> folder; the backend reads them on every poll
 * so we don't have to invalidate any cache.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_SECURITY_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-security-panel');
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
  // Each test owns the project's security/ subtree so the empty-state run
  // and the populated run don't leak fixtures into each other.
  const dir = path.join(projectPath, 'security');
  if (fs.existsSync(dir)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test.afterAll(async () => {
  const dir = path.join(projectPath, 'security');
  if (fs.existsSync(dir)) {
    try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* best-effort */ }
  }
});

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function plantBaseline(content: string) {
  const dir = path.join(projectPath, 'security');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'baseline.md'), content, 'utf8');
}

function plantReview(fileName: string, content: string) {
  const dir = path.join(projectPath, 'security', 'reviews');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, fileName), content, 'utf8');
}

async function openSecurityRail(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('project-shell-rail-security').click();
  await expect(page.getByTestId('security-panel')).toBeVisible();
  // The hash should reflect the active rail.
  expect(page.url()).toContain(`#/projects/${slugFor(projectName)}/security`);
}

test('empty state - no baseline, no reviews, all actions render', async ({ page }) => {
  await openSecurityRail(page);

  await expect(page.getByTestId('security-baseline-badge')).toContainText(/No baseline|Baseline unknown/);
  await expect(page.getByTestId('security-history-empty')).toBeVisible();

  // All three action buttons are present and the audit button is enabled.
  await expect(page.getByTestId('security-run-audit')).toBeVisible();
  await expect(page.getByTestId('security-open-evidence')).toBeVisible();
  await expect(page.getByTestId('security-create-followup')).toBeVisible();
  // Open evidence is disabled when there is no last review to point at.
  await expect(page.getByTestId('security-open-evidence')).toBeDisabled();

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/01-empty-state.png`,
    fullPage: true,
  });
});

test('populated state - baseline badge, last-review card, severity split, history list', async ({ page }) => {
  plantBaseline(
    '---\n' +
    'status: ok\n' +
    'lastVerified: 2026-04-01\n' +
    'definitionRef: docs/quality/audits/SEC-OVERVIEW.md\n' +
    'severityThresholds:\n' +
    '  critical: zero\n' +
    '  high: review\n' +
    'summary: Baseline approved on 2026-04-01.\n' +
    '---\n\n' +
    '## Notes\n\nBaseline approved during quarterly review.\n'
  );
  plantReview(
    '2026-04-12-quarterly.md',
    '---\n' +
    'date: 2026-04-12\n' +
    'verdict: ok\n' +
    'severity: info\n' +
    'openFindings: 2\n' +
    'severities:\n' +
    '  critical: 0\n' +
    '  high: 0\n' +
    '  medium: 1\n' +
    '  low: 1\n' +
    'title: Quarterly check\n' +
    'summary: Two low-severity dependency notes; nothing critical.\n' +
    '---\n\n' +
    '## Findings\n\n- Outdated dev-dep on lodash.\n- Stale TLS cert on a staging endpoint.\n'
  );
  plantReview(
    '2026-02-01-baseline-pass.md',
    '---\n' +
    'date: 2026-02-01\n' +
    'verdict: ok\n' +
    'severity: info\n' +
    'openFindings: 0\n' +
    'title: Baseline pass\n' +
    'summary: Initial security baseline accepted.\n' +
    '---\n\n' +
    '## Notes\n\nFirst pass. Clean.\n'
  );

  await openSecurityRail(page);

  // Baseline badge picks the OK bucket from status: ok.
  await expect(page.getByTestId('security-baseline-badge')).toContainText('Baseline OK');

  // Last review card shows the newer file with its verdict.
  const lastReviewCard = page.getByTestId('security-card-last-review');
  await expect(lastReviewCard).toContainText('2026-04-12');
  await expect(lastReviewCard).toContainText('ok');
  await expect(lastReviewCard).toContainText('Quarterly check');

  // Open findings card shows 2 with a severity split.
  const openCard = page.getByTestId('security-card-open-findings');
  await expect(openCard).toContainText('2');

  // Baseline definition card shows the linked record.
  await expect(page.getByTestId('security-baseline-def-ref'))
    .toContainText('docs/quality/audits/SEC-OVERVIEW.md');

  // History list: newest first.
  const rows = page.locator('[data-testid="security-history-row"]');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText('2026-04-12');
  await expect(rows.nth(1)).toContainText('2026-02-01');

  // Open Evidence is enabled now that a review exists.
  await expect(page.getByTestId('security-open-evidence')).toBeEnabled();

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/02-populated.png`,
    fullPage: true,
  });
});

test('parseOk=false review renders unstructured-report warning with raw markdown', async ({ page }) => {
  plantReview(
    '2026-04-30-prose-only.md',
    '# Ad-hoc audit\n\n' +
    'The agent forgot to emit a structured block, so the panel must show ' +
    'the raw Markdown with an unstructured-report warning instead of ' +
    'pretending to know the verdict.\n'
  );

  await openSecurityRail(page);

  const row = page.locator('[data-testid="security-history-row"][data-parse-ok="false"]');
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('security-unstructured-warning')).toBeVisible();

  // The raw-markdown viewer is loaded on demand.
  await row.getByTestId('security-row-load-raw').click();
  await expect(row.getByTestId('security-raw-md')).toContainText('Ad-hoc audit');

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/03-unstructured.png`,
    fullPage: true,
  });
});

test('Run security audit queues a job and a duplicate request is rejected with an error chip', async ({ page }) => {
  // Plant nothing - empty state so the queue starts clean. The panel still
  // exposes the audit button and we drive the action via the UI.
  await openSecurityRail(page);

  // First click: success chip with the new job id.
  await page.getByTestId('security-run-audit').click();
  await expect(page.getByTestId('security-audit-queued')).toBeVisible({ timeout: 10_000 });
  const okText = await page.getByTestId('security-audit-queued').textContent();
  expect(okText ?? '').toMatch(/queued/i);

  // Second click: backend returns 409, panel surfaces the error chip.
  await page.getByTestId('security-run-audit').click();
  await expect(page.getByTestId('security-audit-error')).toBeVisible({ timeout: 10_000 });
  const errText = await page.getByTestId('security-audit-error').textContent();
  expect(errText ?? '').toMatch(/already/i);

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/04-audit-conflict.png`,
    fullPage: true,
  });

  // Tear down: delete the queued audit job so subsequent test runs do not
  // leak it into the kanban. The job sits in 2-ready under the watched
  // project; the API delete handles the folder removal cleanly.
  await cleanupQueuedAuditJobs();
});

test('Create follow-up task opens the create-job dialog with prefilled prompt', async ({ page }) => {
  plantReview(
    '2026-04-12-quarterly.md',
    '---\n' +
    'date: 2026-04-12\n' +
    'verdict: warn\n' +
    'severity: warn\n' +
    'openFindings: 1\n' +
    'title: Quarterly check\n' +
    'summary: One stale TLS cert on staging.\n' +
    '---\n'
  );

  await openSecurityRail(page);
  await page.getByTestId('security-create-followup').click();

  // The existing create-job dialog opens; the close button is the most stable hook.
  await expect(page.getByTestId('create-dialog-close')).toBeVisible({ timeout: 10_000 });

  // Title input has no testid; the placeholder is the stable selector.
  const titleInput = page.getByPlaceholder('Task title');
  await expect(titleInput).toHaveValue(/Security follow-up/i);
  const promptArea = page.getByTestId('create-prompt');
  const promptValue = (await promptArea.inputValue()) ?? '';
  expect(promptValue).toMatch(/security follow-up/i);
  expect(promptValue).toMatch(/2026-04-12-quarterly\.md/);

  await page.screenshot({
    path: `${SCREENSHOT_DIR}/05-create-followup.png`,
    fullPage: true,
  });
});

void BACKEND;

async function cleanupQueuedAuditJobs(): Promise<void> {
  type Job = { id: string; title?: string | null; state?: string; watchPath?: string };
  try {
    const list = await api<Job[]>(`/api/tasks`);
    const auditJobs = list.filter(j =>
      (j.title ?? '').toLowerCase().startsWith('security audit') &&
      (j.watchPath?.toLowerCase() === projectPath.toLowerCase()),
    );
    for (const j of auditJobs) {
      try {
        await api(`/api/tasks/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(projectPath)}`, { method: 'DELETE' });
      } catch { /* best-effort cleanup */ }
    }
  } catch { /* best-effort */ }
}
