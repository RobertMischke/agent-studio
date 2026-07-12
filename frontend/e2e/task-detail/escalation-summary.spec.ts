import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as path from 'path';
import { mkdirSync, writeFileSync } from 'fs';

/**
 * Escalation summary panel — collapsible + compact (AGT-2060, round 2 on AGT-2019).
 *
 * Round 1 (AGT-2019) shipped a prominent escalation card. Operator feedback:
 * it is a "RIESIGE Karte" that must be collapsible so the rest of the detail
 * view stays reachable. This round makes the panel:
 *   1. COLLAPSIBLE — the whole header row is the toggle; state remembered per task.
 *   2. DEFAULT by lane — open on the acute `5e-escalated` lane, closed elsewhere
 *      (an escalate verdict parked in `5-human-review` is historical context).
 *   3. COMPACT — the header carries a one-line essence (reason category · review
 *      grade · merge status); the gate checklist + detail grid live in the
 *      height-capped, internally-scrolling body shown only when expanded.
 *   4. NO left accent line (operator hard-rule).
 *
 * Fully mocked so it is deterministic and needs no live backend.
 */

const PROJECT = 'fixture-escalation';
const WATCH_PATH = 'C:/fixtures/escalation';
const JOB_ID = 'AGT-1994-fixture';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, '.')
  : path.resolve(__dirname, '../../test-results/escalation-summary');

/** Persist a shot both as a report attachment and as a durable file under results/. */
async function saveShot(testInfo: TestInfo, name: string, body: Buffer): Promise<void> {
  await testInfo.attach(name, { body, contentType: 'image/png' });
  try {
    mkdirSync(SHOTS_DIR, { recursive: true });
  } catch {
    /* best-effort: the attachment above is the fallback */
  }
}

function buildInfo(state: string) {
  return {
    id: JOB_ID,
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'Result-Templates Teil 2: JSON-Meta-Haelfte',
    state,
    orchestratorVerdict: 'escalate',
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
    execution: null,
    kind: 'task',
    epicId: null,
    commit: null,
    commits: [
      { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'first slice', filesChanged: 3, files: ['a.ts', 'b.ts', 'c.ts'], at: '2026-07-09T18:00:00Z' },
      { sha: '1a526e97', shortSha: '1a526e97', message: 'wire it', filesChanged: 2, files: ['c.ts', 'd.ts'], at: '2026-07-09T19:00:00Z' },
    ],
    mergeSignal: {
      branch: 'task/AGT-1994',
      inIntegration: true,
      inRelease: true,
      integrationBranch: 'develop',
      releaseBranch: 'main',
      integrationSha: 'b2ed3f4',
      releaseSha: '1a526e9',
    },
    tags: [],
    ownerClientId: 'local-default',
    lastUsage: null,
  };
}

function buildDetail(state: string) {
  return {
    info: buildInfo(state),
    promptMarkdown: '# Task',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: '# Status\nResult: escalated\n\n- test bullet one\n- test bullet two\n- test bullet three',
    statusGeneration: null,
    contextUsage: null,
    log: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 10 },
    reviewEvidence: [],
  };
}

const FOLLOW_UP = [
  '# Orchestrator follow-up',
  '',
  'STEER THE DIFF, DO NOT RESTART: close out only the open items.',
  '',
  '- [ ] Frontend build/unit/Playwright verification skipped (worktree limitation).',
  '- [ ] Live Haiku probe not run (dev backend offline).',
  '- [ ] Structured JSON aspect artefacts left for follow-up.',
].join('\n');

const CODE_REVIEW_LIST = {
  entries: [
    {
      fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
      verdict: 'pass',
      grade: 'B',
      summary: 'High-quality, wired, well-tested first slice that defers the JSON half and two metrics.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T19:22:02Z',
    },
  ],
};

const TIMELINE = [
  {
    ts: '2026-07-09T19:45:00Z',
    kind: 'orchestrator_escalated',
    actor: 'orchestrator',
    summary: 'escalated',
    details: {
      reason: 'Completion gate found unfinished work in the previous run.',
      cause: 'completion-gate',
      attempt: '3',
      maxAttempts: '3',
    },
  },
];

async function installRoutes(page: Page, state: string): Promise<void> {
  const info = buildInfo(state);
  const detail = buildDetail(state);
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: state === '5-human-review' ? [info] : [],
    escalated: state === '5e-escalated' ? [info] : [],
    completed: [], archive: [],
  };

  // Catch-all first (lowest priority); specific routes registered later win.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  // Shell boot dependencies.
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-09T20:00:00Z', snapshots: [] }) }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));

  // Detail (broad) — must be registered before the narrower sub-routes below.
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));

  // The Overview tab polls `/api/tasks/{id}/pipeline`; answer the real shape
  // (`null` = no pipeline yet) so no shell-error overlay floats over the panel.
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));

  // Narrow sub-routes win over the detail route (registered later).
  await page.route('**/code-review/list**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(CODE_REVIEW_LIST) }));
  await page.route('**/files/orchestrator-follow-up.md**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: FOLLOW_UP }));
  await page.route('**/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(TIMELINE) }));
}

/**
 * Dismiss any transient "Unexpected application error" overlay before capturing
 * (an un-mocked shell poll can raise one and it would bleed into the shot).
 */
async function dismissAppErrorDialog(page: Page): Promise<void> {
  const dialog = page.getByTestId('error-dialog');
  for (let i = 0; i < 3 && (await dialog.isVisible().catch(() => false)); i++) {
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function openDetail(page: Page, state: string): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 960 });
  await installRoutes(page, state);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('escalation-summary')).toBeVisible({ timeout: 20_000 });
}

type ThemeShots = Record<'dark' | 'light', Buffer>;

async function shootBothThemes(page: Page, testInfo: TestInfo, baseName: string): Promise<ThemeShots> {
  const panel = page.getByTestId('escalation-summary');
  const out: Partial<ThemeShots> = {};
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.waitForTimeout(200);
    await dismissAppErrorDialog(page);
    await panel.scrollIntoViewIfNeeded();
    const shot = await panel.screenshot();
    const name = `${baseName}-${theme}--mocked.png`;
    await saveShot(testInfo, name, shot);
    writeFileSync(path.join(SHOTS_DIR, name), shot);
    out[theme] = shot;
  }
  return out as ThemeShots;
}

test.describe('Escalation summary panel — collapsible + compact', () => {
  test.beforeEach(() => test.setTimeout(90_000));

  test('collapses by default off the acute lane, expands on click, carries a one-line essence', async ({ page }, testInfo) => {
    // Fixture is the AGT-1994 shape: escalate verdict parked in 5-human-review.
    await openDetail(page, '5-human-review');

    const panel = page.getByTestId('escalation-summary');
    const toggle = page.getByTestId('escalation-toggle');

    // 1. Default CLOSED off the acute lane; body hidden, header still present.
    await expect(page.getByTestId('escalation-body')).toHaveCount(0);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    // 2. The header carries the compact one-line essence at all times.
    await expect(page.getByTestId('escalation-essence-grade')).toHaveText('B');
    await expect(page.getByTestId('escalation-essence-merge')).toBeVisible();
    // Recommendation stays on the header.
    await expect(page.getByTestId('escalation-recommendation')).toHaveText('Needs decision');

    // 3. No left accent line: the left border matches the 1px all-round border
    //    (round 1 used a 3px severity stripe here — the operator hard-rule bans it).
    const borders = await panel.evaluate((el) => {
      const s = getComputedStyle(el);
      return {
        left: s.borderLeftWidth,
        right: s.borderRightWidth,
        top: s.borderTopWidth,
        bottom: s.borderBottomWidth,
      };
    });
    expect(borders.left).toBe('0px');
    expect(borders.right).toBe('0px');
    expect(borders.top).toBe('1px');
    expect(borders.bottom).toBe(borders.top);

    const geometry = await panel.evaluate((el) => {
      const panelRect = el.getBoundingClientRect();
      const workspaceRect = el.closest('.workspace__main--studio')?.getBoundingClientRect();
      return workspaceRect ? {
        leftDelta: Math.abs(panelRect.left - workspaceRect.left),
        rightDelta: Math.abs(panelRect.right - workspaceRect.right),
      } : null;
    });
    expect(geometry).not.toBeNull();
    expect(geometry!.leftDelta).toBeLessThanOrEqual(1);
    expect(geometry!.rightDelta).toBeLessThanOrEqual(1);

    await dismissAppErrorDialog(page);
    const afterShots = await shootBothThemes(page, testInfo, 'escalation-collapsed');

    // 4. Clicking the header row expands the panel and reveals the details.
    await setTheme(page, 'dark');
    await toggle.click();
    await expect(page.getByTestId('escalation-body')).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');

    // Open gate points from the follow-up checklist.
    await expect(page.locator('[data-testid="escalation-gate-items"] li')).toHaveCount(3);
    await expect(page.getByTestId('escalation-gate-source')).toContainText('follow-up checklist');
    await expect(page.getByTestId('escalation-gate-count')).toContainText('3 open');
    // Review verdict head.
    await expect(page.getByTestId('escalation-review-grade')).toHaveText('B');
    await expect(page.getByTestId('escalation-review-verdict')).toHaveText('pass');
    await expect(page.getByTestId('escalation-review-summary')).toContainText('first slice');
    // Delivery context: deduped file count across both commits.
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('2 commits');
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('4 files');
    // Escalation reason headline from the timeline event.
    await expect(page.getByTestId('escalation-reason')).toContainText('Completion gate');

    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-expanded');

    // 5. Reconstruct the round-1 look (always-open, unbounded, left severity
    //    stripe, full title) by mutating the real panel in place, and shoot it
    //    as the "before". Same component styles, so it is a faithful mock-up of
    //    what the operator saw after AGT-2019 — not a live capture.
    await mutateToRound1(page);
    const beforeShots = await shootBothThemes(page, testInfo, 'escalation-before-round1');

    // 6. Stitch a before/after composite on an isolated page (no app chrome to
    //    bleed through): round-1 giant beside the round-2 compact collapsed head.
    await captureComposite(page, testInfo, beforeShots, afterShots);
  });

  test('defaults open on the acute 5e-escalated lane', async ({ page }, testInfo) => {
    await openDetail(page, '5e-escalated');
    // Acute lane: the operator is here to act, so the panel opens by default.
    await expect(page.getByTestId('escalation-body')).toBeVisible();
    await expect(page.getByTestId('escalation-toggle')).toHaveAttribute('aria-expanded', 'true');
    await dismissAppErrorDialog(page);
    await shootBothThemes(page, testInfo, 'escalation-5e-open');
  });
});

/**
 * Mutate the real (expanded) panel in place back to the round-1 presentation:
 * always-open, unbounded height, the left severity stripe the operator hard-rule
 * now bans, and the full title with no compact essence line. Reuses the live
 * component styles, so the element shot is a faithful mock-up of the AGT-2019
 * look for the before/after comparison.
 */
async function mutateToRound1(page: Page): Promise<void> {
  await page.evaluate(() => {
    const panel = document.querySelector('[data-testid="escalation-summary"]') as HTMLElement | null;
    if (!panel) return;
    panel.style.borderLeft = '3px solid var(--severity-high)';
    panel.querySelector('.escalation__chevron')?.remove();
    panel.querySelector('.escalation__essence')?.remove();
    const title = panel.querySelector('.escalation__title');
    if (title) title.textContent = 'Escalation — operator decision needed';
    const body = panel.querySelector('[data-testid="escalation-body"]') as HTMLElement | null;
    if (body) body.style.maxHeight = 'none';
  });
}

/**
 * Stitch a before/after composite on an isolated blank page (no live app chrome
 * to bleed into the shot): the round-1 giant beside the round-2 compact collapsed
 * header. The two source shots are already rendered per theme, so we only lay the
 * PNGs out side by side on a theme-matched backdrop.
 */
async function captureComposite(
  page: Page,
  testInfo: TestInfo,
  before: ThemeShots,
  after: ThemeShots,
): Promise<void> {
  const backdrop = { dark: '#1e1e2e', light: '#eff1f5' } as const;
  const caption = { dark: '#a6adc8', light: '#5c5f77' } as const;
  for (const theme of ['dark', 'light'] as const) {
    const b64 = (buf: Buffer) => buf.toString('base64');
    await page.setContent(
      `<!doctype html><html><body style="margin:0;background:${backdrop[theme]};padding:24px;`
      + `display:flex;gap:24px;align-items:flex-start;font-family:system-ui,sans-serif">`
      + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
      + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">Before · round 1 (AGT-2019)</figcaption>`
      + `<img alt="before" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(before[theme])}"></figure>`
      + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
      + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">After · round 2 (AGT-2060)</figcaption>`
      + `<img alt="after" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(after[theme])}"></figure>`
      + `</body></html>`,
    );
    await page.waitForTimeout(100);
    const shot = await page.screenshot({ fullPage: true });
    const name = `before-after-${theme}--composite-mocked.png`;
    await saveShot(testInfo, name, shot);
    writeFileSync(path.join(SHOTS_DIR, name), shot);
  }
}
