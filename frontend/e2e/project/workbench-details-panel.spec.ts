import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Naming Evidence';
const WATCH_PATH = 'C:/evidence/naming';
const WORKBENCH_ID = 'naming-dossier';
const WORKBENCH_KEY = 'AGT-W33';
const RESULTS = resolve(
  process.env['JOB_RESULTS_DIR'] ?? resolve(__dirname, '..', '..', '..', 'results', 'AGT-2610'),
);

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const DOSSIER_HTML = `<main>
  <h1>Naming Dossier</h1>
  <p>Select the stable public naming contract.</p>
  <section data-decision-id="public-name" data-decision-kind="single">
    <h2>Choose the public name</h2>
    <ul>
      <li data-option-id="stable-key">Stable key</li>
      <li data-option-id="display-name">Display name</li>
    </ul>
    <label>Reason <textarea data-comment="Naming reason"></textarea></label>
  </section>
</main>`;

interface CapturedCalls {
  taskBodies: Record<string, unknown>[];
  decisionBodies: Record<string, unknown>[];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installMocks(page: Page): Promise<CapturedCalls> {
  const captured: CapturedCalls = { taskBodies: [], decisionBodies: [] };
  let taskCreated = false;
  let decision: Record<string, unknown> | null = null;
  let decisionStage: string | null = null;
  let revision = '0123456789abcdef';
  let fingerprint = 'a'.repeat(64);

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'workspace-evidence', displayName: 'Evidence', sortOrder: 0, isDefault: true,
    projects: [{
      id: 'project-naming', displayName: PROJECT, shortCode: 'NDS',
      workspaceId: 'workspace-evidence', storageLocation: WATCH_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-11T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-11T10:00:00Z', sessions: [],
  }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => json(route, {
    models: [], source: 'workbench-details-evidence',
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'codex', model: 'gpt-5', thinkingLevel: null,
  }));
  await page.route('**/api/cli/model-routing/recommendation**', route => json(route, {
    model: 'gpt-5', thinkingLevel: null, tier: 'complex', taskType: 'feature',
    economyDowngraded: false, policyVersion: 'e2e',
    policyWikiPath: 'docs/system/domains/model-routing-policy.md',
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/workbenches**', route => json(route, {
    includesHistory: true,
    count: 1,
    items: [{
      projectName: PROJECT,
      workbench: {
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: 'active', phase: 'decision-ready',
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
      },
    }],
  }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches`,
    route => json(route, {
      projectName: PROJECT,
      includesHistory: false,
      count: 1,
      items: [{
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: 'active', phase: 'decision-ready',
        updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html',
        valid: true, error: null, sourceTaskKeys: ['AGT-2600'],
      }],
    }),
  );
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, taskCreated ? {
    ...EMPTY_GROUPED,
    preparation: [{
      id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
      taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
      state: '1-preparation', projectName: PROJECT, watchPath: WATCH_PATH,
    }],
  } : EMPTY_GROUPED));
  await page.route('**/api/tasks/reference-status', route => json(route, {
    items: taskCreated ? [{
      key: 'AGT-2611', exists: true, taskKey: `${PROJECT}::AGT-2611`,
      title: 'Implement the stable naming contract', lane: '1-preparation',
      projectId: 'project-naming', projectName: PROJECT, projectColor: null,
      merge: null, reviewGrade: null,
    }] : [],
  }));
  await page.route(/\/api\/tasks(?:\?.*)?$/, route => {
    if (route.request().method() !== 'POST') return json(route, []);
    const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    captured.taskBodies.push(body);
    taskCreated = true;
    return json(route, { id: 'naming-feature-1' });
  });
  await page.route(/\/api\/tasks\/naming-feature-1(?:\?.*)?$/, route => json(route, {
    info: {
      id: 'naming-feature-1', key: 'AGT-2611', displayKey: 'AGT-2611',
      taskKey: `${PROJECT}::AGT-2611`, title: 'Implement the stable naming contract',
      state: '1-preparation', projectName: PROJECT, watchPath: WATCH_PATH,
    },
  }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_KEY}/references`,
    route => json(route, {
      projectName: PROJECT, workbenchKey: WORKBENCH_KEY, workbenchId: WORKBENCH_ID,
      legacyTaskKeys: [], items: [],
    }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/prepare`,
    route => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.decisionBodies.push(body);
      revision = '1234567890abcdef';
      fingerprint = 'b'.repeat(64);
      decisionStage = 'prepared';
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome: body['outcome'], decisionStage,
        revision, fingerprint, spawnedTaskKeys: [], responses: body['responses'],
        taskDraft: body['task'], idempotent: false,
      });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}/decisions/confirm`,
    route => {
      const body = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
      captured.decisionBodies.push(body);
      revision = '2345678901abcdef';
      fingerprint = 'c'.repeat(64);
      decisionStage = 'succeeded';
      decision = {
        outcome: 'feature-spawn', state: 'succeeded', operationId: body['operationId'],
        sourceRevision: body['expectedRevision'], sourceFingerprint: body['expectedFingerprint'],
        preparedAt: '2026-08-11T10:01:00Z', preparedBy: body['actor'],
        confirmedAt: '2026-08-11T10:02:00Z', confirmedBy: body['actor'],
        decidedAt: '2026-08-11T10:02:00Z', reason: null, failure: null,
        spawnedTaskKeys: body['spawnedTaskKeys'], responses: body['responses'],
        taskDraft: body['task'],
      };
      return json(route, {
        success: true, errorCode: null, error: null, workbenchId: WORKBENCH_ID,
        operationId: body['operationId'], outcome: 'feature-spawn', decisionStage,
        revision, fingerprint, spawnedTaskKeys: body['spawnedTaskKeys'],
        responses: body['responses'], taskDraft: body['task'], idempotent: false,
      });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    route => json(route, {
      workbench: {
        id: WORKBENCH_ID, key: WORKBENCH_KEY, title: 'Naming Dossier',
        summary: 'Choose the stable naming contract used by public references.',
        status: decisionStage === 'succeeded' ? 'decided' : 'active',
        phase: 'decision-ready', updatedAtUtc: '2026-08-11T10:00:00Z',
        entryPath: 'docs/operations/naming-dossier/index.html', valid: true, error: null,
        sourceTaskKeys: ['AGT-2600'], relatedTaskKeys: taskCreated ? ['AGT-2611'] : [],
        lifecycleState: decisionStage === 'succeeded' ? 'decided' : 'review-requested',
        decision, decisionStage,
      },
      html: DOSSIER_HTML, branch: 'develop', revision,
      workingTreeModified: false, fingerprint,
    }),
  );

  return captured;
}

async function seedWorkbench(page: Page) {
  await page.addInitScript(({ project, workbenchId }) => {
    if (!sessionStorage.getItem('agt2610-workbench-seeded')) {
      sessionStorage.clear();
      sessionStorage.setItem('agt2610-workbench-seeded', '1');
    }
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'workbench', projectName: project, workbenchId, title: 'Naming Dossier' }],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('defaultCliType', 'claude');
    localStorage.setItem('defaultModel:claude', 'claude-opus-4-6');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
}

async function openDetails(page: Page) {
  await page.getByTestId('workbench-viewer-details-trigger').click();
  await expect(page.getByTestId('workbench-viewer-details-popover')).toBeVisible();
}

async function capturePanel(page: Page, theme: 'light' | 'dark', state: 'before' | 'after') {
  await setTheme(page, theme);
  const panel = page.getByTestId('workbench-viewer-details-popover');
  const clip = await panel.boundingBox();
  expect(clip).not.toBeNull();
  await page.screenshot({
    path: resolve(RESULTS, `workbench-details-panel-${state}-${theme}--mocked.png`),
    clip: clip!,
  });
}

test('Dossier details keeps a session draft and creates the feature card', async ({ page }) => {
  const captureBefore = process.env['CAPTURE_WORKBENCH_DETAILS_BEFORE'] === '1';
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1000 });
  const captured = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });

  const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const stableOption = frame.locator('[data-option-id="stable-key"] input');
  await expect(stableOption).toBeVisible();
  await openDetails(page);

  if (captureBefore) {
    await expect(page.getByTestId('workbench-viewer-open-wiki')).toHaveCount(2);
    await expect(page.getByTestId('workbench-decision-panel')
      .getByTestId('workbench-key-chip')).toBeVisible();
    await page.getByRole('button', { name: 'Close details' }).click();
    await stableOption.check();
    await frame.locator('[data-studio-decision-comment]').fill('Keep the public key stable.');
    await openDetails(page);
    await page.getByTestId('workbench-decision-prepare').click();
    await expect(page.getByTestId('workbench-decision-title')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await capturePanel(page, theme, 'before');
    }
    return;
  }

  await expect(page.getByTestId('workbench-viewer-open-wiki')).toHaveCount(0);
  await expect(page.getByTestId('workbench-key-chip')).toHaveCount(0);
  await expect(page.getByTestId('workbench-decision-prepare')).toBeDisabled();

  await stableOption.check();
  await frame.locator('[data-studio-decision-comment]').fill('Keep the public key stable.');
  await openDetails(page);
  await expect(page.getByTestId('workbench-decision-draft-notice')).toBeVisible();
  await page.getByTestId('workbench-decision-prepare').click();
  await page.getByTestId('workbench-decision-title').fill('Implement the stable naming contract');
  await page.getByTestId('workbench-decision-goal').fill(
    'Apply the selected stable key across public references and navigation.',
  );

  await page.getByRole('button', { name: 'Close details' }).click();
  await expect(page.getByTestId('workbench-viewer-details-popover')).toBeHidden();
  await openDetails(page);
  await expect(page.getByTestId('workbench-decision-title')).toHaveValue(
    'Implement the stable naming contract',
  );

  await page.reload();
  await expect(page.getByTestId('workbench-viewer')).toBeVisible({ timeout: 30_000 });
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .locator('[data-option-id="stable-key"] input')).toBeChecked();
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .locator('[data-studio-decision-comment]')).toHaveValue('Keep the public key stable.');
  await openDetails(page);
  await expect(page.getByTestId('workbench-decision-title')).toHaveValue(
    'Implement the stable naming contract',
  );
  await expect(page.getByTestId('workbench-decision-goal')).toHaveValue(
    'Apply the selected stable key across public references and navigation.',
  );
  await expect(page.getByTestId('workbench-decision-discard')).toBeVisible();

  for (const theme of ['light', 'dark'] as const) {
    await capturePanel(page, theme, 'after');
    const colours = await page.getByTestId('workbench-decision-title').evaluate(element => {
      const style = getComputedStyle(element);
      return { color: style.color, background: style.backgroundColor };
    });
    expect(
      contrastRatio(colours.color, colours.background),
      `${theme} title field contrast`,
    ).toBeGreaterThanOrEqual(4.5);
  }

  await page.getByTestId('workbench-decision-confirm').click();
  const created = page.getByTestId('workbench-decision-created-tasks');
  await expect(created).toContainText('AGT-2611');
  await expect(created).toContainText('Implement the stable naming contract');
  await expect(created).toContainText('Preparation');
  await expect(page.getByTestId('workbench-decision-draft-notice')).toHaveCount(0);

  expect(captured.decisionBodies).toHaveLength(2);
  expect(captured.taskBodies).toEqual([expect.objectContaining({
    title: 'Implement the stable naming contract',
    watchPath: WATCH_PATH,
    targetState: '1-preparation',
    taskType: 'feature',
  })]);
  expect(captured.decisionBodies[0]).toEqual(expect.objectContaining({
    outcome: 'feature-spawn',
    expectedRevision: '0123456789abcdef',
    expectedFingerprint: 'a'.repeat(64),
    task: expect.objectContaining({
      title: 'Implement the stable naming contract',
      goal: 'Apply the selected stable key across public references and navigation.',
    }),
    responses: [expect.objectContaining({
      selectedOptionIds: ['stable-key'],
      comment: 'Keep the public key stable.',
    })],
  }));
  expect(captured.decisionBodies[1]).toEqual(expect.objectContaining({
    confirmed: true,
    expectedRevision: '1234567890abcdef',
    expectedFingerprint: 'b'.repeat(64),
    spawnedTaskKeys: ['AGT-2611'],
  }));
});
