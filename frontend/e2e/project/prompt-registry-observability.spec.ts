import { expect, test } from '../fixtures/dev-backend';
import { setTheme, type Theme } from '../helpers/theme';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';

interface WatchPath {
  name: string;
}

interface ReviewRun {
  reviewedCount: number;
  findingCount: number;
  results: {
    name: string;
    metadata: {
      status: string;
      findings: { code: string }[];
    };
  }[];
}

const resultsDir = process.env.JOB_RESULTS_DIR ?? 'test-results';
const identityHeaders = {
  'Content-Type': 'application/json',
  'X-Client-Id': 'local-default',
};

test('prompt registry reviews shipped prompts and exposes provenance, overrides, calls, and cost', async ({
  page,
  devBackend,
}) => {
  test.setTimeout(120_000);
  mkdirSync(resultsDir, { recursive: true });
  const watchPaths = await fetch(`${devBackend.baseUrl}/api/watch-paths`)
    .then(response => response.json()) as WatchPath[];
  const project = watchPaths[0]?.name;
  expect(project, 'the dev-backend fixture supplies one isolated watched project').toBeTruthy();

  const pipelineStepUrl =
    `${devBackend.baseUrl}/api/projects/${encodeURIComponent(project!)}/pipeline-step`;
  const reviewUrl = `${devBackend.baseUrl}/api/admin/prompts/review-all`;
  const runtimePromptDir = join(devBackend.workspace, 'prompts', 'runtime');
  const originalSidecars = new Map(
    readdirSync(runtimePromptDir)
      .filter(name => name.endsWith('.md.meta.json'))
      .map(name => [name, readFileSync(join(runtimePromptDir, name), 'utf8')]),
  );

  try {
    const override = await fetch(pipelineStepUrl, {
      method: 'PUT',
      headers: identityHeaders,
      body: JSON.stringify({
        stepId: 'aspect-code-quality',
        prompt: 'E2E project-specific code quality review prompt.',
      }),
    });
    expect(override.ok, await override.text()).toBe(true);

    const reviewed = await fetch(reviewUrl, {
      method: 'POST',
      headers: identityHeaders,
      body: JSON.stringify({ reviewedBy: 'runtime-prompt-audit' }),
    });
    const reviewedBody = await reviewed.text();
    if (!reviewed.ok) {
      throw new Error(
        `review-all failed with HTTP ${reviewed.status}: ${reviewedBody || '(empty body)'}`,
      );
    }
    const run = JSON.parse(reviewedBody) as ReviewRun;
    expect(run.reviewedCount).toBeGreaterThan(0);
    expect(run.findingCount).toBeGreaterThan(0);
    const deadPrompt = run.results.find(item =>
      item.name === 'recurring-output-pattern-review.md');
    expect(deadPrompt?.metadata.status).toBe('stale');
    expect(deadPrompt?.metadata.findings.some(finding => finding.code === 'dead-prompt'))
      .toBe(true);

    const deadSidecar = join(
      devBackend.workspace,
      'prompts',
      'runtime',
      'recurring-output-pattern-review.md.meta.json',
    );
    expect(existsSync(deadSidecar)).toBe(true);
    expect(readFileSync(deadSidecar, 'utf8')).toContain('"code": "dead-prompt"');

    await page.setViewportSize({ width: 2560, height: 1200 });
    await page.goto('/#/workspace/settings/prompts', {
      waitUntil: 'domcontentloaded',
      timeout: 30_000,
    });
    const crashRecovery = page.getByTestId('crash-recovery-prompt');
    if (await crashRecovery.isVisible({ timeout: 3_000 }).catch(() => false)) {
      await page.getByTestId('crash-recovery-dismiss-all').click();
      await expect(crashRecovery).toBeHidden();
    }
    const landing = page.getByTestId('prompt-admin-landing');
    await expect(landing).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('prompt-admin-overview-table')).toContainText('Calls total / 7d');
    await expect(page.getByTestId('prompt-admin-overview-table')).toContainText('Last change');
    await expect(page.getByTestId('prompt-admin-overview-table')).toContainText('Last review');
    await expect(page.getByTestId('prompt-admin-overview-table')).toContainText('Cost total / 7d');
    await expect(page.getByTestId('prompt-admin-class-runtime-step')).toBeVisible();
    await expect(page.getByTestId('prompt-admin-class-orchestrator')).toBeVisible();
    await expect(page.getByTestId('prompt-admin-class-drift')).toBeVisible();
    await expect(page.getByTestId('prompt-admin-class-framing')).toBeVisible();

    // A delayed workspace recovery observation can appear after the page's
    // initial recovery-dialog check has already completed.
    if (await crashRecovery.isVisible().catch(() => false)) {
      await page.getByTestId('crash-recovery-dismiss-all').click();
      await expect(crashRecovery).toBeHidden();
    }

    await page.getByTestId('prompt-admin-landing-link-review-aspect-code-quality.md').click();
    const projectOverrideRow = page.getByTestId(
      `prompt-admin-project-override-${project}-aspect-code-quality`,
    );
    await expect(projectOverrideRow).toBeVisible();
    await expect(projectOverrideRow).toContainText(project!);
    await expect(page.getByTestId('prompt-admin-status')).toContainText('last change:');
    await expect(page.getByTestId('prompt-admin-review')).toContainText('last review:');
    await expect(page.getByTestId('prompt-admin-call-telemetry')).toBeVisible();

    for (const theme of ['dark', 'light'] as Theme[]) {
      await setTheme(page, theme);
      await page.screenshot({
        path: join(resultsDir, `prompt-registry-detail-${theme}.png`),
        fullPage: true,
      });
    }

    await page.getByTestId('prompt-admin-home').click();
    await expect(landing).toBeVisible();
    for (const theme of ['dark', 'light'] as Theme[]) {
      await setTheme(page, theme);
      await page.screenshot({
        path: join(resultsDir, `prompt-registry-overview-${theme}.png`),
        fullPage: true,
      });
    }
  } finally {
    await fetch(pipelineStepUrl, {
      method: 'PUT',
      headers: identityHeaders,
      body: JSON.stringify({ stepId: 'aspect-code-quality' }),
    });
    for (const name of readdirSync(runtimePromptDir).filter(
      candidate => candidate.endsWith('.md.meta.json'),
    )) {
      if (!originalSidecars.has(name)) rmSync(join(runtimePromptDir, name));
    }
    for (const [name, content] of originalSidecars) {
      writeFileSync(join(runtimePromptDir, name), content);
    }
  }
});
