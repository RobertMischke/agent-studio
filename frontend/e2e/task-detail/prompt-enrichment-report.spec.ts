import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import * as path from 'node:path';

const JOB_ID = 'prompt-enrichment-fixture';
const WATCH_PATH = '/fixtures/prompt-enrichment';
const RESULTS_DIR = path.resolve(__dirname, '../../results/AGT-2411');

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function detail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `AGT-E2E-${JOB_ID}`,
      displayKey: 'AGT-2411',
      title: 'Prompt enrichment preprocessing',
      state: '3-progress',
      order: 1,
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.6-sol',
      thinkingLevel: 'high',
      watchPath: WATCH_PATH,
      projectName: 'Agent Taskboard',
      folderPath: `${WATCH_PATH}/3-progress/${JOB_ID}`,
      createdAt: '2026-07-28T09:00:00Z',
      lastActivity: '2026-07-28T09:01:00Z',
      sessionName: null,
      useOwnSession: null,
      lastUsage: null,
      execution: null,
      commit: null,
      commits: [],
      tags: ['frontend', 'delegation'],
      references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    },
    promptMarkdown: [
      '# Original operator prompt',
      '',
      'Add prompt preprocessing while keeping this authored card unchanged and readable.',
      '',
      'Acceptance: show the report next to this text.',
    ].join('\n'),
    enrichmentReport: {
      schemaVersion: '1.0',
      enrichmentId: 'e2e-enrichment',
      generatedAtUtc: '2026-07-28T09:00:02Z',
      status: 'enriched',
      originalPromptSha256: 'aaa',
      enrichedPromptSha256: 'bbb',
      policy: {
        id: 'prompt-enrichment',
        version: '1',
        projectEnabled: true,
        selector: 'constraint-selector-v4-token-economy',
        tokenizer: 'character-estimate-v1',
        tokenBudget: 1500,
        optionalBlockLimit: 2,
        styleGuideSnapshotId: 'style-snapshot-42',
      },
      detectedAreas: ['frontend', 'delegation'],
      candidates: [
        {
          id: 'repo-instructions-source',
          title: 'Use repository instructions and indexed docs',
          source: 'AGENTS.md; docs/start/README.md',
          signals: ['general'],
          decision: 'appended',
          reason: 'mandatory-project-policy',
          estimatedTokens: 78,
        },
        {
          id: 'delegation-economy',
          title: 'Keep delegation bounded and evidence-driven',
          source: 'AGENTS.md#Product-Boundaries',
          signals: ['delegation'],
          decision: 'appended',
          reason: 'matched-task-area',
          estimatedTokens: 64,
        },
        {
          id: 'style-guide:frontend-styling',
          title: 'Frontend styling context',
          source: 'docs/quality/frontend-styling.md',
          signals: ['frontend'],
          decision: 'appended',
          reason: 'matched-task-area',
          estimatedTokens: 72,
        },
      ],
      appendedBlocks: [
        {
          id: 'repo-instructions-source',
          title: 'Use repository instructions and indexed docs',
          source: 'AGENTS.md; docs/start/README.md',
          revision: '1',
          digestSha256: 'ccc',
          tier: 'mandatory-project-policy',
          order: 1,
          estimatedTokens: 78,
          exactContent: '- **Use repository instructions and indexed docs** (`repo-instructions-source`)',
        },
        {
          id: 'delegation-economy',
          title: 'Keep delegation bounded and evidence-driven',
          source: 'AGENTS.md#Product-Boundaries',
          revision: '1',
          digestSha256: 'ddd',
          tier: 'optional',
          order: 2,
          estimatedTokens: 64,
          exactContent: '- **Keep delegation bounded and evidence-driven** (`delegation-economy`)',
        },
        {
          id: 'style-guide:frontend-styling',
          title: 'Frontend styling context',
          source: 'docs/quality/frontend-styling.md',
          revision: '7',
          digestSha256: 'eee',
          tier: 'optional',
          order: 3,
          estimatedTokens: 72,
          exactContent: '- **Frontend styling context** (`style-guide:frontend-styling`)',
        },
      ],
      tokens: {
        tokenizer: 'character-estimate-v1',
        original: 186,
        appended: 328,
        final: 514,
        preprocessingInput: 0,
        preprocessingOutput: 0,
        preprocessingCacheRead: 0,
        preprocessingCacheCreation: 0,
      },
      cost: {
        currency: 'USD',
        selectorUsd: 0,
        appendedInputUsd: 0.0007,
        estimateModel: 'gpt-5.6-sol',
      },
      timingMs: 4,
      warnings: [],
      errors: [],
    },
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const encodedId = encodeURIComponent(JOB_ID);
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local',
    bootstrapRequired: false,
    authenticated: true,
    user: null,
  })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [detail().info],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    review: [],
    completed: [],
    archive: [],
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: 'Agent Taskboard', path: WATCH_PATH, rootPath: WATCH_PATH },
  ])));
  await page.route('**/api/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({
    snapshots: [],
    ttlSeconds: 600,
  })));
  await page.route(`**/api/tasks/${encodedId}/runs**`, route =>
    route.fulfill(json({ runs: [], runnerEvents: [] })));
  await page.route(`**/api/tasks/${encodedId}/session-events**`, route =>
    route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(`**/api/tasks/${encodedId}/pipeline**`, route =>
    route.fulfill(json({
      pipeline: { pre: [], core: [], post: [], allSteps: [] },
      execution: null,
      executions: [],
      config: {},
      cost: null,
    })));
  await page.route(`**/api/tasks/${encodedId}?**`, route => route.fulfill(json(detail())));
}

for (const theme of ['light', 'dark'] as const) {
  test(`shows the prompt enrichment report beside the authored prompt (${theme})`, async ({ page }) => {
    await page.addInitScript(selectedTheme => {
      localStorage.setItem('atp.studio.theme', selectedTheme);
      document.documentElement.dataset['studioTheme'] = selectedTheme;
    }, theme);
    await installRoutes(page);
    await page.setViewportSize({ width: 1600, height: 1000 });
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await page.getByTestId('inspector-tab-task').click();
    const prompt = page.getByTestId('task-tab-prompt');
    const report = page.getByTestId('enrichment-report');
    await expect(prompt).toContainText('Original operator prompt');
    await expect(prompt).toContainText('keeping this authored card unchanged');
    await expect(report).toBeVisible();
    await expect(report).toContainText('Enrichment report');
    await expect(report).toContainText('Enriched');
    await expect(report).toContainText('Original186');
    await expect(report).toContainText('Appended+328');
    await expect(report).toContainText('Final514');
    await expect(report).toContainText('Selector: 0 tokens');
    await expect(report).toContainText('delegation');
    await expect(report).toContainText('Frontend styling context');

    const promptBox = await prompt.boundingBox();
    const reportBox = await report.boundingBox();
    expect(promptBox).not.toBeNull();
    expect(reportBox).not.toBeNull();
    expect(reportBox!.x).toBeGreaterThan(promptBox!.x + promptBox!.width);

    await mkdir(RESULTS_DIR, { recursive: true });
    await page.screenshot({
      path: path.join(RESULTS_DIR, `prompt-enrichment-report-${theme}.png`),
      fullPage: false,
    });
  });
}
