import { expect, test } from '@playwright/test';
import type { Page, Route, TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Viewer Header Evidence';
const WORKBENCH_ID = 'compact-viewer-header';
const WORKBENCH_KEY = 'VHE-W4';
const WATCH_PATH = 'C:/evidence/viewer-header';

// Route mocks are the complete backend for this visual contract. A production
// build's service worker would bypass page.route after activation.
test.use({ serviceWorkers: 'block' });

const EMPTY_GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
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
    dependsOn: [],
    relatedTo: [],
    blockedBy: [],
    supersedes: [],
    workbenches: [WORKBENCH_KEY],
  },
};

interface RefreshCapture {
  linked: boolean;
  taskRequest: Record<string, unknown> | null;
  referenceRequest: Record<string, unknown> | null;
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installMocks(page: Page): Promise<RefreshCapture> {
  const capture: RefreshCapture = {
    linked: false,
    taskRequest: null,
    referenceRequest: null,
  };
  await page.route('**/healthz', (route) => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/auth/status', (route) =>
    json(route, {
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  );
  await page.route('**/api/watch-paths', (route) =>
    json(route, [
      {
        name: PROJECT,
        path: WATCH_PATH,
        rootPath: WATCH_PATH,
        repositoryPath: WATCH_PATH,
      },
    ]),
  );
  await page.route('**/api/workspaces**', (route) =>
    json(route, [
      {
        id: 'ws-viewer-evidence',
        displayName: 'Evidence',
        sortOrder: 0,
        isDefault: true,
        projects: [
          {
            id: 'project-viewer-evidence',
            displayName: PROJECT,
            shortCode: 'VHE',
            workspaceId: 'ws-viewer-evidence',
            storageLocation: WATCH_PATH,
            sortOrder: 0,
            archived: false,
            urls: [],
          },
        ],
      },
    ]),
  );
  await page.route('**/api/environment**', (route) =>
    json(route, {
      isDev: false,
      devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
    }),
  );
  await page.route('**/api/runner/status**', (route) => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', (route) =>
    json(route, {
      at: '2026-08-09T10:00:00Z',
      snapshots: [],
      ttlSeconds: 600,
    }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    json(route, {
      at: '2026-08-09T10:00:00Z',
      sessions: [],
    }),
  );
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, (route) =>
    json(route, {
      models: [],
      source: 'viewer-header-evidence',
    }),
  );
  await page.route('**/api/cli/maintenance-model', (route) =>
    json(route, {
      cliType: 'codex',
      model: 'gpt-5',
      thinkingLevel: null,
    }),
  );
  await page.route('**/api/crash-recovery/pending**', (route) => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', (route) =>
    json(route, {
      items: [],
      total: 0,
      offset: 0,
      limit: 50,
    }),
  );
  await page.route('**/api/tasks/grouped**', (route) =>
    json(route, {
      ...EMPTY_GROUPED,
      progress: [primaryTask],
    }),
  );
  await page.route('**/api/tasks', (route) => {
    capture.taskRequest = JSON.parse(route.request().postData() ?? '{}') as Record<string, unknown>;
    return json(route, { id: 'refresh-dossier' });
  });
  await page.route('**/api/tasks/refresh-dossier/references**', (route) => {
    capture.referenceRequest = JSON.parse(
      route.request().postData() ?? '{}',
    ) as Record<string, unknown>;
    capture.linked = true;
    return json(route, {
      references: capture.referenceRequest,
      warnings: [],
    });
  });
  await page.route('**/api/tasks/reference-status', (route) => {
    const request = JSON.parse(route.request().postData() ?? '{"keys":[]}') as { keys?: string[] };
    const statuses = new Map([
      ['VHE-11', { title: primaryTask.title, taskKey: primaryTask.taskKey, lane: '3-progress' }],
      [
        'VHE-12',
        {
          title: 'Prepare header contract',
          taskKey: `${PROJECT}::prepare-header`,
          lane: '2-ready',
        },
      ],
      [
        'VHE-13',
        {
          title: 'Review compact interaction',
          taskKey: `${PROJECT}::review-header`,
          lane: '5-human-review',
        },
      ],
      [
        'VHE-14',
        {
          title: 'Refresh: Compact viewer header keeps operational context in one quiet line',
          taskKey: `${PROJECT}::refresh-dossier`,
          lane: '1-preparation',
        },
      ],
    ]);
    return json(route, {
      items: (request.keys ?? []).flatMap((key) => {
        const status = statuses.get(key);
        return status
          ? [
              {
                key,
                exists: true,
                ...status,
                projectId: 'project-viewer-evidence',
                projectName: PROJECT,
                projectColor: '#a78bfa',
                merge: null,
                reviewGrade: null,
              },
            ]
          : [];
      }),
    });
  });
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/wiki/home`, (route) =>
    json(route, {
      sections: [],
    }),
  );
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches`, (route) =>
    json(route, {
      projectName: PROJECT,
      includesHistory: false,
      count: 1,
      items: [
        {
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
        },
      ],
    }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_ID}`,
    (route) =>
      json(route, {
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
        },
        html: `<!doctype html><html><body><main>
        <h1>Compact viewer header</h1>
        <p>The document remains the primary reading surface.</p>
        <section data-decision-id="route" data-decision-kind="single"><strong>Route</strong><span data-option-id="direct">Direct</span></section>
        <section data-decision-id="density" data-decision-kind="single"><strong>Density</strong><span data-option-id="compact">Compact</span></section>
        <section data-decision-id="proof" data-decision-kind="confirm"><strong>Proof</strong><span data-option-id="capture">Capture</span></section>
      </main></body></html>`,
        branch: 'task/compact-viewer-header',
        revision: '1234567890abcdef',
        workingTreeModified: false,
        fingerprint: 'a'.repeat(64),
      }),
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${WORKBENCH_KEY}/references`,
    (route) =>
      json(route, {
        projectName: PROJECT,
        workbenchKey: WORKBENCH_KEY,
        workbenchId: WORKBENCH_ID,
        legacyTaskKeys: ['VHE-12', 'VHE-13'],
        items: [
          {
            sourceKey: 'VHE-11',
            sourceJobId: primaryTask.id,
            sourceTitle: primaryTask.title,
            sourceState: primaryTask.state,
            sourceWatchPath: WATCH_PATH,
            kind: 'workbenches',
          },
          ...(capture.linked
            ? [{
                sourceKey: 'VHE-14',
                sourceJobId: 'refresh-dossier',
                sourceTitle: 'Refresh: Compact viewer header keeps operational context in one quiet line',
                sourceState: '1-preparation',
                sourceWatchPath: WATCH_PATH,
                kind: 'workbenches',
              }]
            : []),
        ],
      }),
  );
  return capture;
}

async function seedWorkbench(page: Page): Promise<void> {
  await page.addInitScript(
    ({ project, workbenchId }) => {
      const tab = {
        kind: 'workbench',
        projectName: project,
        workbenchId,
        title: 'Compact viewer header',
      };
      localStorage.setItem(
        'atp.studio.tabs.v1',
        JSON.stringify({
          v: 1,
          tabs: [tab],
          activeKey: `workbench:${project}:${workbenchId}`,
        }),
      );
      localStorage.setItem('atp.studio.theme', 'light');
    },
    { project: PROJECT, workbenchId: WORKBENCH_ID },
  );
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

test('compact viewer head keeps live card state and details usable in both themes and widths', async ({
  page,
}, testInfo) => {
  const capture = await installMocks(page);
  await seedWorkbench(page);
  await page.goto('/');
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  const header = page.getByTestId('workbench-viewer-header');
  await expect(header).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('workbench-viewer-key')).toContainText(WORKBENCH_KEY);
  await expect(page.getByTestId('workbench-viewer-open-decisions')).toContainText('3 open');
  await expect(page.getByTestId('workbench-viewer-implementation-status')).toHaveText(
    'In implementation',
  );
  await expect(page.getByTestId(/^workbench-viewer-task-VHE-(11|12|13)$/)).toHaveCount(3);
  await page.getByTestId('workbench-viewer-task-VHE-11').hover();
  await expect(page.getByTestId('workbench-viewer-task-VHE-11-tooltip')).toContainText(
    'In progress',
  );

  for (const viewport of [
    { label: 'wide', width: 1700, height: 1000 },
    { label: 'narrow', width: 1180, height: 820 },
  ]) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await expect
      .poll(async () => (await header.boundingBox())?.height ?? 999)
      .toBeLessThanOrEqual(48);
    const titleBox = await page.getByTestId('workbench-viewer-title').boundingBox();
    const statusBox = await page.getByTestId('workbench-viewer-status').boundingBox();
    expect(
      Math.abs(
        (titleBox?.y ?? 0) +
          (titleBox?.height ?? 0) / 2 -
          ((statusBox?.y ?? 0) + (statusBox?.height ?? 0) / 2),
      ),
    ).toBeLessThan(4);

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

  await details.getByRole('button', { name: 'Close details' }).click();
  await setTheme(page, 'light');
  await page.getByTestId('workbench-viewer-refresh').click();
  const confirmation = page.getByTestId('confirm-dialog');
  await expect(confirmation).toBeVisible();
  await expect(confirmation).toContainText('Create Dossier refresh card?');
  await expect(confirmation).toContainText(
    'Refresh: Compact viewer header keeps operational context in one quiet line',
  );
  await expect(confirmation).toContainText(WORKBENCH_KEY);
  await expect(confirmation).toContainText('docs/operations/compact-viewer-header/index.html');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-refresh-confirmation-light--mocked.png'),
    fullPage: true,
  });
  await confirmation.getByTestId('confirm-dialog-confirm').click();

  await expect(page.getByTestId('workbench-viewer-task-VHE-14')).toBeVisible();
  expect(capture.taskRequest).toMatchObject({
    title: 'Refresh: Compact viewer header keeps operational context in one quiet line',
    watchPath: WATCH_PATH,
    targetState: '1-preparation',
    taskType: 'chore',
    mode: 'coding',
  });
  expect(String(capture.taskRequest?.['promptMarkdown'])).toContain(
    'Dossier path: `docs/operations/compact-viewer-header/index.html`',
  );
  expect(String(capture.taskRequest?.['promptMarkdown'])).toContain(`Dossier key: \`${WORKBENCH_KEY}\``);
  expect(String(capture.taskRequest?.['promptMarkdown'])).toContain(
    'Update the document against reality (incorporate findings, mark completed sections, refresh figures).',
  );
  expect(capture.referenceRequest).toEqual({
    dependsOn: [],
    relatedTo: [],
    blockedBy: [],
    supersedes: [],
    workbenches: [WORKBENCH_KEY],
  });

  await setTheme(page, 'dark');
  await captureViewerTop(page, testInfo, 'dossier-refresh-card-dark--mocked.png');
});
