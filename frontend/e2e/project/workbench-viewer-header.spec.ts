import { expect, test } from '@playwright/test';
import type { Page, Route, TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Viewer Header Evidence';
const WORKBENCH_ID = 'compact-viewer-header';
const WORKBENCH_KEY = 'VHE-W4';
const WATCH_PATH = 'C:/evidence/viewer-header';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
  escalated: [], review: [], completed: [], archive: [],
};

const primaryTask = {
  id: 'compact-header-task',
  key: 'VHE-11',
  displayKey: 'VHE-11',
  taskKey: `${PROJECT}::compact-header-task`,
  title: 'Implement compact viewer header',
  state: '3-progress',
  order: 1,
  agent: 'codex',
  createdAt: '2026-08-09T09:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/3-progress/compact-header-task`,
  lastActivity: '2026-08-09T10:00:00Z',
  sessionName: null,
  model: null,
  cliType: 'codex',
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
  references: {
    dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [WORKBENCH_KEY],
  },
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installMocks(page: Page): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'ws-viewer-evidence', displayName: 'Evidence', sortOrder: 0, isDefault: true,
    projects: [{
      id: 'project-viewer-evidence', displayName: PROJECT, shortCode: 'VHE',
      workspaceId: 'ws-viewer-evidence', storageLocation: WATCH_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-09T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-09T10:00:00Z', sessions: [],
  }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => json(route, {
    models: [], source: 'viewer-header-evidence',
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'codex', model: 'gpt-5', thinkingLevel: null,
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    ...EMPTY_GROUPED,
    progress: [primaryTask],
  }));
  await page.route('**/api/tasks/reference-status', route => {
    const request = JSON.parse(route.request().postData() ?? '{"keys":[]}') as { keys?: string[] };
    const statuses = new Map([
      ['VHE-11', { title: primaryTask.title, taskKey: primaryTask.taskKey, lane: '3-progress' }],
      ['VHE-12', { title: 'Prepare header contract', taskKey: `${PROJECT}::prepare-header`, lane: '2-ready' }],
      ['VHE-13', { title: 'Review compact interaction', taskKey: `${PROJECT}::review-header`, lane: '5-human-review' }],
    ]);
    return json(route, {
      items: (request.keys ?? []).flatMap(key => {
        const status = statuses.get(key);
        return status ? [{
          key, exists: true, ...status, projectId: 'project-viewer-evidence',
          projectName: PROJECT, projectColor: '#a78bfa', merge: null, reviewGrade: null,
        }] : [];
      }),
    });
  });
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/home`, route => json(route, {
    sections: [],
  }));
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches`, route => json(route, {
    projectName: PROJECT,
    includesHistory: false,
    count: 1,
    items: [{
      id: WORKBENCH_ID,
      key: WORKBENCH_KEY,
      title: 'Compact viewer header',
      summary: 'Source metadata and controls stay in a detail popover.',
      status: 'decision-pending',
      phase: 'decision-ready',
      updatedAtUtc: '2026-08-09T10:00:00Z',
      entryPath: 'docs/operations/compact-viewer-header/index.html',
      valid: true,
      error: null,
      sourceTaskKeys: [],
      relatedTaskKeys: ['VHE-12', 'VHE-13'],
      openDecisionCount: 3,
    }],
  }));
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    route => json(route, {
      workbench: {
        id: WORKBENCH_ID,
        key: WORKBENCH_KEY,
        title: 'Compact viewer header keeps operational context in one quiet line',
        summary: 'Source metadata, actions, and decision controls move into a detail popover.',
        status: 'decision-pending',
        phase: 'decision-ready',
        updatedAtUtc: '2026-08-09T10:00:00Z',
        entryPath: 'docs/operations/compact-viewer-header/index.html',
        valid: true,
        error: null,
        sourceTaskKeys: [],
        relatedTaskKeys: ['VHE-12', 'VHE-13'],
        openDecisionCount: 3,
      },
      html: `<!doctype html><html><body><main>
        <h1>Compact viewer header</h1>
        <p>The document remains the primary reading surface.</p>
      </main></body></html>`,
      branch: 'task/compact-viewer-header',
      revision: '1234567890abcdef',
      workingTreeModified: false,
      fingerprint: 'a'.repeat(64),
    }),
  );
}

async function seedWorkbench(page: Page): Promise<void> {
  await page.addInitScript(({ project, workbenchId }) => {
    const tab = { kind: 'workbench', projectName: project, workbenchId, title: 'Compact viewer header' };
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [tab],
      activeKey: `workbench:${project}:${workbenchId}`,
    }));
    localStorage.setItem('atp.studio.theme', 'light');
  }, { project: PROJECT, workbenchId: WORKBENCH_ID });
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const root = resolve(process.env['JOB_RESULTS_DIR'] ?? testInfo.outputDir);
  mkdirSync(root, { recursive: true });
  return resolve(root, fileName);
}

async function captureViewerTop(page: Page, testInfo: TestInfo, fileName: string): Promise<void> {
  const header = page.getByTestId('workbench-viewer-header');
  const box = await header.boundingBox();
  expect(box).not.toBeNull();
  await page.screenshot({
    path: evidencePath(testInfo, fileName),
    clip: box!,
  });
}

test('compact viewer head keeps live card state and details usable in both themes and widths', async ({ page }, testInfo) => {
  await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });

  const header = page.getByTestId('workbench-viewer-header');
  await expect(header).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('workbench-viewer-key')).toContainText(WORKBENCH_KEY);
  await expect(page.getByTestId('workbench-viewer-open-decisions')).toContainText('3 open');
  await expect(page.getByTestId(/^workbench-viewer-task-VHE-(11|12|13)$/)).toHaveCount(3);
  await page.getByTestId('workbench-viewer-task-VHE-11').hover();
  await expect(page.getByTestId('workbench-viewer-task-VHE-11-tooltip'))
    .toContainText('In progress');

  for (const viewport of [
    { label: 'wide', width: 1700, height: 1000 },
    { label: 'narrow', width: 1180, height: 820 },
  ]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await expect.poll(async () => (await header.boundingBox())?.height ?? 999)
      .toBeLessThanOrEqual(48);
    const titleBox = await page.getByTestId('workbench-viewer-title').boundingBox();
    const statusBox = await page.getByTestId('workbench-viewer-status').boundingBox();
    expect(Math.abs(
      ((titleBox?.y ?? 0) + (titleBox?.height ?? 0) / 2)
      - ((statusBox?.y ?? 0) + (statusBox?.height ?? 0) / 2),
    )).toBeLessThan(4);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await captureViewerTop(
        page,
        testInfo,
        `workbench-viewer-header-${theme}-${viewport.label}--mocked.png`,
      );
    }
  }

  await page.getByTestId('workbench-viewer-details-trigger').click();
  const details = page.getByTestId('workbench-viewer-details-popover');
  await expect(details).toBeVisible();
  await expect(details).toContainText('Source metadata, actions, and decision controls');
  await expect(details).toContainText('docs/operations/compact-viewer-header/index.html');
  await expect(details.getByTestId('workbench-decision-panel')).toBeVisible();
});
