import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * In-product concept docs: the small "i" trigger next to a panel title
 * opens a popover with a short paragraph plus a "Learn more" link to the
 * matching doc under docs/. The component is wired into five panels:
 * the global orchestrator card (concept = orchestrator), the project
 * supervisor section (concept = supervisor), the project security panel
 * (concept = audits-and-checks), the project skill-readiness section
 * (concept = skills), and the CLI usage detail modal (concept = probes).
 *
 * The spec verifies that each trigger renders, opens the popover with a
 * non-empty body and a Learn-more link pointing to the expected docs path,
 * and closes again when the user hits Escape.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CONCEPT_HELP_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'concept-help');
})();

let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

async function assertConceptHelp(
  page: import('@playwright/test').Page,
  concept: string,
  expectedDocsPath: string,
) {
  const trigger = page.getByTestId(`concept-help-trigger-${concept}`);
  await expect(trigger).toBeVisible();
  await trigger.click();

  const popover = page.getByTestId(`concept-help-popover-${concept}`);
  await expect(popover).toBeVisible();

  const body = page.getByTestId(`concept-help-body-${concept}`);
  const bodyText = (await body.textContent())?.trim() ?? '';
  expect(bodyText.length).toBeGreaterThan(40);

  const learn = page.getByTestId(`concept-help-learn-${concept}`);
  await expect(learn).toBeVisible();
  const href = await learn.getAttribute('href');
  expect(href).toContain(expectedDocsPath);

  const pathChip = page.getByTestId(`concept-help-path-${concept}`);
  await expect(pathChip).toContainText(expectedDocsPath);

  // Close via Escape and confirm the popover unmounts.
  await page.keyboard.press('Escape');
  await expect(popover).toHaveCount(0);
}

test('orchestrator concept-help on the global orchestrator card', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  // The global orchestrator card lives under the Orchestrator rail of the
  // project shell (see project-detail.html `view === 'orchestrator'`).
  await page.getByTestId('project-shell-rail-orchestrator').click();
  const card = page.getByTestId('global-orchestrator-card');
  await card.scrollIntoViewIfNeeded();
  await expect(card).toBeVisible({ timeout: 10_000 });
  await assertConceptHelp(page, 'orchestrator', 'docs/architecture-decisions.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/01-orchestrator.png`,
    fullPage: true,
  });
});

test('supervisor concept-help on the project supervisor section', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  // The supervisor section lives under the Observability rail (see
  // project-detail.html `view === 'observability'`).
  await page.getByTestId('project-shell-rail-observability').click();
  const section = page.getByTestId('project-supervisor-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();
  await assertConceptHelp(page, 'supervisor', 'docs/architecture-decisions.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/02-supervisor.png`,
    fullPage: true,
  });
});

test('audits-and-checks concept-help on the project security panel', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('project-shell-rail-security').click();
  await expect(page.getByTestId('security-panel')).toBeVisible();
  await assertConceptHelp(page, 'audits-and-checks', 'docs/security/overview.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/03-audits-and-checks.png`,
    fullPage: true,
  });
});

test('skills concept-help on the project skill-readiness section', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('project-shell-rail-steering').click();
  const section = page.getByTestId('project-skill-readiness-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();
  await assertConceptHelp(page, 'skills', 'docs/skills-architecture.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/04-skills.png`,
    fullPage: true,
  });
});

test('probes concept-help on the CLI usage detail modal', async ({ page }) => {
  await page.goto('/');
  // The status-bar trigger opens the CLI usage detail modal which carries
  // the probes concept-help next to the title.
  const trigger = page.getByTestId('usage-hover-panel');
  await expect(trigger).toBeVisible({ timeout: 10_000 });
  await trigger.click();
  await expect(page.getByTestId('hquota-modal')).toBeVisible();
  await assertConceptHelp(page, 'probes', 'docs/cli-skills/cli-overview.md');
  await page.screenshot({
    path: `${SCREENSHOT_DIR}/05-probes.png`,
    fullPage: true,
  });
});
