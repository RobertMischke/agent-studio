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
  releaseWorkbench: () => void;
}

interface DossierFixture {
  key: string;
  title: string;
  heading: string;
  summary: string;
  entryPath: string;
  html: string;
}

interface WorkbenchMockOptions {
  workbenchError?: {
    status: number;
    message: string;
  };
  dossier?: DossierFixture;
  deferWorkbench?: boolean;
  overviewStyleEvidence?: boolean;
}

const DEFAULT_DOSSIER: DossierFixture = {
  key: WORKBENCH_KEY,
  title: 'Compact viewer header keeps operational context in one quiet line',
  heading: 'Compact viewer header',
  summary: 'Source metadata, actions, and decision controls move into a detail popover.',
  entryPath: 'docs/operations/compact-viewer-header/index.html',
  html: `<!doctype html><html><body><main>
    <h1>Compact viewer header</h1>
    <p>The document remains the primary reading surface.</p>
    <section data-decision-id="route" data-decision-kind="single"><strong>Route</strong><span data-option-id="direct">Direct</span></section>
    <section data-decision-id="density" data-decision-kind="single"><strong>Density</strong><span data-option-id="compact">Compact</span></section>
    <section data-decision-id="proof" data-decision-kind="confirm"><strong>Proof</strong><span data-option-id="capture">Capture</span></section>
  </main></body></html>`,
};

function scrollingDossierFixture(
  metadata: Omit<DossierFixture, 'html'>,
  sectionCount: number,
): DossierFixture {
  const sections = Array.from({ length: sectionCount }, (_, index) => `
    <section>
      <h2>${String(index + 1).padStart(2, '0')} · Evidence section</h2>
      <p>Repository evidence remains readable inside the document viewport after asynchronous loading.</p>
      <p>The viewer owns the available height while this document owns its vertical scroll position.</p>
    </section>`).join('');
  return {
    ...metadata,
    html: `<!doctype html><html><head><style>
      :root { color-scheme: light dark; --bg: #fcfcfb; --fg: #11110f; --muted: #5f5e59; --line: #d8d6cf; }
      @media (prefers-color-scheme: dark) { :root { --bg: #1a1a19; --fg: #f7f6f2; --muted: #b7b6ae; --line: #3d3d3a; } }
      * { box-sizing: border-box; }
      body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 system-ui, sans-serif; }
      main { width: min(100% - 48px, 960px); margin: 0 auto; padding: 36px 0 64px; }
      h1 { margin: 0 0 12px; font-size: 34px; }
      h2 { margin: 0 0 8px; font-size: 20px; }
      p { max-width: 76ch; color: var(--muted); }
      section { min-height: 150px; padding: 28px 0; border-top: 1px solid var(--line); }
      footer { padding-top: 28px; border-top: 1px solid var(--line); font-weight: 700; }
    </style></head><body><main>
      <h1 data-testid="dossier-start">${metadata.heading}</h1>
      <p>${metadata.summary}</p>
      ${sections}
      <footer data-testid="dossier-end">Complete dossier · ${metadata.key}</footer>
    </main></body></html>`,
  };
}

const HEIGHT_DOSSIERS = [
  {
    label: 'short',
    fixture: scrollingDossierFixture({
      key: 'QS-W5',
      title: 'Quality Studio Test Baseline',
      heading: 'A test baseline that stays green',
      summary: 'A compact quality dossier with enough evidence to require internal scrolling.',
      entryPath: 'docs/operations/test-baseline/index.html',
    }, 8),
  },
  {
    label: 'long',
    fixture: scrollingDossierFixture({
      key: 'AGT-W1',
      title: 'Admin Surface Design Guideline',
      heading: 'Admin Surface Design Guideline',
      summary: 'A long Agent Studio dossier that exercises the same viewer height contract.',
      entryPath: 'docs/operations/admin-design-guideline/index.html',
    }, 24),
  },
] as const;

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installMocks(
  page: Page,
  options: WorkbenchMockOptions = {},
): Promise<RefreshCapture> {
  const dossier = options.dossier ?? DEFAULT_DOSSIER;
  let releaseWorkbench = () => undefined;
  const workbenchReady = options.deferWorkbench
    ? new Promise<void>((resolveWorkbench) => {
        releaseWorkbench = resolveWorkbench;
      })
    : Promise.resolve();
  const capture: RefreshCapture = {
    linked: false,
    taskRequest: null,
    referenceRequest: null,
    releaseWorkbench,
  };
  const overviewItems = options.overviewStyleEvidence
    ? [
        {
          projectName: PROJECT,
          projectShortCode: 'VHE',
          projectColor: '#a78bfa',
          workbench: {
            id: WORKBENCH_ID,
            key: dossier.key,
            title: dossier.title,
            summary: dossier.summary,
            status: 'decision-pending',
            phase: 'decision-ready',
            updatedAtUtc: '2026-08-09T10:00:00Z',
            entryPath: dossier.entryPath,
            valid: true,
            error: null,
            sourceTaskKeys: [],
            relatedTaskKeys: ['VHE-12', 'VHE-13'],
            openDecisionCount: 3,
          },
        },
        {
          projectName: 'Operations Console',
          projectShortCode: 'OPS',
          projectColor: '#89b4fa',
          workbench: {
            id: 'runner-placement',
            key: 'OPS-W2',
            title: 'Choose the remote runner placement policy',
            summary: 'Compare the available host boundaries before recording the rollout policy.',
            status: 'decision-pending',
            phase: 'decision-ready',
            updatedAtUtc: '2026-08-08T14:30:00Z',
            entryPath: 'docs/operations/runner-placement/index.html',
            valid: true,
            error: null,
            sourceTaskKeys: [],
            openDecisionCount: 1,
          },
        },
        {
          projectName: PROJECT,
          projectShortCode: 'VHE',
          projectColor: '#a78bfa',
          workbench: {
            id: 'viewer-navigation',
            key: 'VHE-W3',
            title: 'Dossier navigation model',
            summary: 'Keep document reading and repository navigation in one coherent workspace.',
            status: 'active',
            phase: 'testing',
            updatedAtUtc: '2026-08-07T09:15:00Z',
            entryPath: 'docs/operations/viewer-navigation/index.html',
            valid: true,
            error: null,
            sourceTaskKeys: [],
            openDecisionCount: 0,
          },
        },
        {
          projectName: PROJECT,
          projectShortCode: 'VHE',
          projectColor: '#a78bfa',
          workbench: {
            id: 'discarded-layout',
            key: 'VHE-W1',
            title: 'Floating Dossier cards',
            summary: 'The framed card direction was discarded after the operator review.',
            status: 'archived',
            phase: null,
            updatedAtUtc: '2026-08-04T11:20:00Z',
            entryPath: 'docs/operations/discarded-layout/index.html',
            valid: true,
            error: null,
            sourceTaskKeys: [],
            openDecisionCount: 0,
          },
        },
        {
          projectName: 'Operations Console',
          projectShortCode: 'OPS',
          projectColor: '#89b4fa',
          workbench: {
            id: 'documented-runtime',
            key: 'OPS-W1',
            title: 'Runtime evidence retention',
            summary: 'The approved retention contract is now part of the operations documentation.',
            status: 'documented',
            phase: null,
            updatedAtUtc: '2026-08-03T16:45:00Z',
            entryPath: 'docs/operations/runtime-evidence/index.html',
            valid: true,
            error: null,
            sourceTaskKeys: [],
            openDecisionCount: 0,
          },
        },
      ]
    : [
        {
          projectName: PROJECT,
          projectShortCode: 'VHE',
          projectColor: '#a78bfa',
          workbench: {
            id: WORKBENCH_ID,
            key: dossier.key,
            title: dossier.title,
            summary: dossier.summary,
            status: 'decision-pending',
            phase: 'decision-ready',
            updatedAtUtc: '2026-08-09T10:00:00Z',
            entryPath: dossier.entryPath,
            valid: true,
            error: null,
            sourceTaskKeys: [],
            relatedTaskKeys: ['VHE-12', 'VHE-13'],
            openDecisionCount: 3,
          },
        },
      ];
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
  await page.route(/\/api\/workbenches(?:\?.*)?$/, (route) => {
    const requestedProject = new URL(route.request().url()).searchParams.get('project');
    const items = requestedProject
      ? overviewItems.filter(item => item.projectName === requestedProject)
      : overviewItems;
    return json(route, {
      projectName: requestedProject,
      count: items.length,
      currentCount: items.filter(item =>
        ['active', 'decision-pending', 'decided'].includes(item.workbench.status)).length,
      historyCount: items.filter(item =>
        ['archived', 'documented'].includes(item.workbench.status)).length,
      items,
    });
  });
  await page.route(`**/api/projects/${encodeURIComponent(PROJECT)}/workbenches`, (route) =>
    json(route, {
      projectName: PROJECT,
      includesHistory: false,
      count: 1,
      items: [
        {
          id: WORKBENCH_ID,
          key: dossier.key,
          title: dossier.title,
          summary: dossier.summary,
          status: 'decision-pending',
          phase: 'decision-ready',
          updatedAtUtc: '2026-08-09T10:00:00Z',
          entryPath: dossier.entryPath,
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
    async (route) => {
      await workbenchReady;
      if (options.workbenchError) {
        return route.fulfill({
          status: options.workbenchError.status,
          contentType: 'application/json',
          body: JSON.stringify({ error: options.workbenchError.message }),
        });
      }
      return json(route, {
        workbench: {
          id: WORKBENCH_ID,
          key: dossier.key,
          title: dossier.title,
          summary: dossier.summary,
          status: 'decision-pending',
          phase: 'decision-ready',
          updatedAtUtc: '2026-08-09T10:00:00Z',
          entryPath: dossier.entryPath,
          valid: true,
          error: null,
          sourceTaskKeys: [],
          relatedTaskKeys: ['VHE-12', 'VHE-13'],
        },
        html: dossier.html,
        branch: 'task/compact-viewer-header',
        revision: '1234567890abcdef',
        workingTreeModified: false,
        fingerprint: 'a'.repeat(64),
      });
    },
  );
  await page.route(
    `**/api/projects/${encodeURIComponent(PROJECT)}/workbenches/${dossier.key}/references`,
    (route) =>
      json(route, {
        projectName: PROJECT,
        workbenchKey: dossier.key,
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

async function seedWorkbenchOverview(page: Page): Promise<void> {
  await page.addInitScript(
    ({ project }) => {
      const tab = {
        kind: 'workbenches',
        projectName: project,
      };
      localStorage.setItem(
        'atp.studio.tabs.v1',
        JSON.stringify({
          v: 1,
          tabs: [tab],
          activeKey: `workbenches:${project}`,
        }),
      );
      localStorage.setItem('atp.studio.theme', 'light');
    },
    { project: PROJECT },
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

async function expectViewerSettled(page: Page): Promise<void> {
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible({ timeout: 30_000 });
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByRole('heading', { name: 'Compact viewer header' })).toBeVisible();
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);
}

async function expectViewerHeightAndInternalScroll(page: Page): Promise<void> {
  const viewer = page.getByTestId('workbench-viewer');
  const geometry = await viewer.evaluate((element) => {
    const header = element.querySelector<HTMLElement>('[data-testid="workbench-viewer-header"]');
    const shell = element.querySelector<HTMLElement>('[data-testid="workbench-viewer-frame-shell"]');
    const frame = element.querySelector<HTMLIFrameElement>('[data-testid="workbench-viewer-frame"]');
    if (!header || !shell || !frame) throw new Error('Viewer geometry nodes are missing.');
    return {
      viewerHeight: element.getBoundingClientRect().height,
      viewerClientHeight: element.clientHeight,
      viewerScrollHeight: element.scrollHeight,
      headerHeight: header.getBoundingClientRect().height,
      shellHeight: shell.getBoundingClientRect().height,
      frameHeight: frame.getBoundingClientRect().height,
    };
  });
  expect(geometry.viewerHeight).toBeGreaterThan(650);
  expect(geometry.shellHeight).toBeGreaterThan(
    geometry.viewerHeight - geometry.headerHeight - 2,
  );
  expect(Math.abs(geometry.frameHeight - geometry.shellHeight)).toBeLessThanOrEqual(1);
  expect(geometry.viewerScrollHeight).toBeLessThanOrEqual(geometry.viewerClientHeight + 1);

  const isolatedRoot = page.frameLocator('[data-testid="workbench-viewer-frame"]').locator('html');
  const scroll = await isolatedRoot.evaluate(() => ({
    clientHeight: document.documentElement.clientHeight,
    scrollHeight: document.documentElement.scrollHeight,
  }));
  expect(scroll.scrollHeight).toBeGreaterThan(scroll.clientHeight);
  await isolatedRoot.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
    .getByTestId('dossier-end')).toBeInViewport();
}

for (const dossier of HEIGHT_DOSSIERS) {
  test(`${dossier.fixture.key} ${dossier.label} dossier fills the viewer after late load and scrolls internally`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const capture = await installMocks(page, {
      dossier: dossier.fixture,
      deferWorkbench: true,
    });
    await page.goto(
      `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
    );
    await page.addStyleTag({
      content: '[data-testid="offline-banner"] { display: none !important; }',
    });
    await expect(page.getByTestId('workbench-viewer-loading')).toBeVisible({ timeout: 30_000 });

    capture.releaseWorkbench();
    await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible({ timeout: 30_000 });
    const frame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
    await expect(frame.getByTestId('dossier-start')).toContainText(dossier.fixture.heading);
    await expectViewerHeightAndInternalScroll(page);

    await frame.locator('html').evaluate(() => window.scrollTo(0, 0));
    for (const theme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme: theme });
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(
          testInfo,
          `dossier-height-${dossier.label}-${theme}--mocked.png`,
        ),
        fullPage: true,
      });
    }
  });
}

test('overview entry settles without a persistent loading surface', async ({
  page,
}, testInfo) => {
  await installMocks(page);
  await seedWorkbenchOverview(page);
  await page.goto('/');
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  await expect(page.getByTestId(`workbench-overview-item-${PROJECT}-${WORKBENCH_ID}`))
    .toBeVisible({ timeout: 30_000 });
  await page.getByTestId(`workbench-overview-full-${PROJECT}-${WORKBENCH_ID}`).click();
  await expectViewerSettled(page);
  await setTheme(page, 'light');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-overview-entry-settled-light--mocked.png'),
    fullPage: true,
  });
});

test('Dossier overview keeps decision and history lists calm across scopes, themes, and widths', async ({
  page,
}, testInfo) => {
  const evidencePhase = process.env['DOSSIER_EVIDENCE_PHASE'] === 'before' ? 'before' : 'after';
  await installMocks(page, { overviewStyleEvidence: true });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
  });

  for (const scope of [
    { label: 'central', route: '/#/workbenches', expectedItems: 5 },
    {
      label: 'project',
      route: '/#/projects/viewer-header-evidence/workbenches',
      expectedItems: 3,
    },
  ]) {
    await page.goto(scope.route);
    await page.addStyleTag({
      content: '[data-testid="offline-banner"] { display: none !important; }',
    });
    const overview = page.getByTestId('workbench-overview');
    await expect(overview).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('workbench-overview-current-count'))
      .toContainText(`${scope.expectedItems} total`);
    await page.getByTestId('workbench-overview-discarded-toggle').click();
    await page.getByTestId('workbench-overview-completed-toggle').click();

    if (evidencePhase === 'after') {
      const pending = page.getByTestId(`workbench-overview-item-${PROJECT}-${WORKBENCH_ID}`);
      await expect(pending).not.toContainText('Decision pending');
      await expect(pending.getByTestId(`workbench-overview-project-${PROJECT}-${WORKBENCH_ID}`))
        .toContainText('VHE');
      await expect(pending.getByTestId(`workbench-overview-open-count-${PROJECT}-${WORKBENCH_ID}`))
        .toContainText('3 open decisions');
      await expect(page.getByTestId('workbench-overview-discarded-list'))
        .toContainText('The framed card direction was discarded');

      const surface = await pending.evaluate((element) => {
        const style = getComputedStyle(element);
        return {
          borderLeft: style.borderLeftWidth,
          borderRight: style.borderRightWidth,
          radius: style.borderRadius,
          shadow: style.boxShadow,
        };
      });
      expect(surface).toEqual({
        borderLeft: '0px',
        borderRight: '0px',
        radius: '0px',
        shadow: 'none',
      });

      const openFull = page.getByTestId(`workbench-overview-full-${PROJECT}-${WORKBENCH_ID}`);
      const reviewHere = page.getByTestId(`workbench-overview-open-${PROJECT}-${WORKBENCH_ID}`);
      expect(await openFull.evaluate(element => getComputedStyle(element).borderTopWidth)).toBe('0px');
      expect(await reviewHere.evaluate(element => getComputedStyle(element).backgroundColor))
        .not.toBe('rgba(0, 0, 0, 0)');
    }

    for (const viewport of [
      { label: 'wide', width: 1680, height: 1100 },
      { label: 'narrow', width: 760, height: 1100 },
    ]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.screenshot({
          path: evidencePath(
            testInfo,
            `dossier-overview-${evidencePhase}-${scope.label}-${theme}-${viewport.label}--mocked.png`,
          ),
          fullPage: true,
        });
      }
    }
  }
});

test('direct dossier route settles without a persistent loading surface', async ({
  page,
}, testInfo) => {
  await installMocks(page);
  await page.goto(
    `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
  );
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });
  await expect(page).toHaveURL(
    /#\/projects\/viewer-header-evidence\/workbenches\/compact-viewer-header(?:&|$)/,
  );
  await expectViewerSettled(page);
  await setTheme(page, 'dark');
  await page.screenshot({
    path: evidencePath(testInfo, 'dossier-direct-entry-settled-dark--mocked.png'),
    fullPage: true,
  });
});

test('unreadable dossier replaces loading feedback with the backend reason', async ({
  page,
}, testInfo) => {
  await installMocks(page, {
    workbenchError: {
      status: 404,
      message: 'Dossier not found, invalid, or path rejected',
    },
  });
  await page.goto(
    `/#/projects/viewer-header-evidence/workbenches/${encodeURIComponent(WORKBENCH_ID)}`,
  );
  await page.addStyleTag({
    content: '[data-testid="offline-banner"] { display: none !important; }',
  });

  const error = page.getByTestId('workbench-viewer-error');
  await expect(error).toBeVisible({ timeout: 30_000 });
  await expect(error).toContainText('Dossier not found, invalid, or path rejected');
  await expect(page.getByTestId('workbench-viewer-loading')).toHaveCount(0);
  await expect(page.getByTestId('loading-surface-list')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-load-error-${theme}--mocked.png`),
      fullPage: true,
    });
  }
});

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
