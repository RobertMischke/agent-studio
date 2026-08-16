import { expect, test } from '../fixtures/dev-backend';
import type { TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2665 regression: a Dossier's `<img src="assets/foo.png">` used to
 * render as a broken image (the isolated-HTML srcdoc forced `base
 * href="about:blank"` and a `img-src data:`-only CSP, so a relative sibling
 * asset reference could not resolve or load). This drives the real
 * `docs/operations/timeline-redesign/` fixture (AGT-W35), which ships real
 * screenshots under its own `assets/` folder, through the actual app and
 * asserts the images decode inside the sandboxed iframe.
 */
function evidencePath(testInfo: TestInfo, fileName: string): string {
  const jobResultsDir = process.env['JOB_RESULTS_DIR']?.trim();
  if (!jobResultsDir) return testInfo.outputPath(fileName);
  const resultsDir = path.resolve(jobResultsDir);
  fs.mkdirSync(resultsDir, { recursive: true });
  return path.join(resultsDir, fileName);
}

test('Dossier screenshots under assets/ render inside the isolated viewer instead of 404ing (AGT-2665)', async (
  { page, devBackend },
  testInfo,
) => {
  test.setTimeout(120_000);
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const paths = await pathsResponse.json() as { name: string }[];
  let projectName: string | null = null;
  for (const candidate of paths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches`,
    );
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string }[] };
    if (catalogue.items?.some(item => item.id === 'timeline-redesign')) {
      projectName = candidate.name;
      break;
    }
  }
  expect(projectName, 'The real backend must expose the AGT-W35 timeline-redesign Dossier.').not.toBeNull();

  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.goto('/');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
  await expect(workbenchesRow).toBeVisible();
  if (await workbenchesRow.getAttribute('aria-expanded') === 'false') await workbenchesRow.click();
  await page.getByTestId(`studio-explorer-workbench-${projectName}-timeline-redesign`).click();

  const frame = page.getByTestId('workbench-viewer-frame');
  await expect(frame).toBeVisible();
  const dossierFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');

  for (const theme of ['light', 'dark'] as const) {
    const screenshot = dossierFrame.locator(
      `img[alt*="AGT-2577 Timeline in the ${theme} theme"]`,
    ).first();
    await expect(screenshot).toBeVisible();
    await screenshot.scrollIntoViewIfNeeded();
    // A broken/blocked image reports naturalWidth 0; a decoded one does not.
    await expect.poll(() => screenshot.evaluate(
      (img: HTMLImageElement) => img.complete && img.naturalWidth > 0,
    )).toBe(true);
    const src = await screenshot.getAttribute('src');
    expect(src).toMatch(/^https?:\/\/[^/]+\/api\/projects\/[^/]+\/wiki\/assets\/operations\/timeline-redesign\/assets\//);
  }

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('studio-editor').screenshot({
      path: evidencePath(testInfo, `dossier-asset-images-after-${theme}--real.png`),
    });
  }
});
