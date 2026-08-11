import { expect, test, type BrowserContext, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const ENABLED = process.env['RUN_TIMELINE_PERF'] === '1';
const PHASE = process.env['TIMELINE_PERF_PHASE'] === 'before' ? 'before' : 'after';
const EXPECT_WINDOWED = process.env['TIMELINE_PERF_EXPECT_WINDOWED'] === '1';
const SOURCE_API = process.env['TIMELINE_PERF_DATA_API'] ?? 'http://127.0.0.1:5031';
const RESULTS_DIR = path.resolve(process.env['JOB_RESULTS_DIR'] ?? '../results');
const TARGET_COUNT = 1_000;
const WINDOW_COUNT = 50;

interface TimelineEvent {
  ts: string;
  kind: string;
  actor: string;
  runId?: string | null;
  payloadRef?: string | null;
  summary: string;
  details?: Record<string, string> | null;
}

interface Scenario {
  key: string;
  id: string;
  watchPath: string;
  baselineEventCount: number;
}

const SCENARIOS: readonly Scenario[] = [
  {
    key: 'AGT-2577',
    id: 'kontext-chats-s6-zentrale-chat-verwaltung---alle-kontexte-mit-kurz-summary',
    watchPath: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard',
    baselineEventCount: 42,
  },
  {
    key: 'QS-72',
    id: 'implement-dossier-recommendations-static-analysis',
    watchPath: 'C:\\Projects\\agent-taskboard-workspace\\projects\\PROJ-016',
    baselineEventCount: 57,
  },
];

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { headers: { 'X-Client-Id': 'local-default' } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${url}`);
  return await response.json() as T;
}

async function loadScenario(scenario: Scenario) {
  const query = `watchPath=${encodeURIComponent(scenario.watchPath)}`;
  const [detail, sourceEvents] = await Promise.all([
    getJson<Record<string, unknown>>(`${SOURCE_API}/api/tasks/${encodeURIComponent(scenario.id)}?${query}`),
    getJson<TimelineEvent[]>(`${SOURCE_API}/api/tasks/${encodeURIComponent(scenario.id)}/timeline?${query}`),
  ]);
  const baselineEvents = sourceEvents.slice(0, scenario.baselineEventCount);
  if (baselineEvents.length !== scenario.baselineEventCount) {
    throw new Error(`${scenario.key} has ${sourceEvents.length} timeline events, expected at least ${scenario.baselineEventCount}`);
  }
  return { detail, sourceEvents: baselineEvents, events: scaleEvents(baselineEvents, TARGET_COUNT) };
}

function scaleEvents(source: readonly TimelineEvent[], count: number): TimelineEvent[] {
  return Array.from({ length: count }, (_, index) => {
    const original = source[index % source.length];
    const cycle = Math.floor(index / source.length);
    const sourceTime = new Date(original.ts).getTime();
    const ts = new Date((Number.isFinite(sourceTime) ? sourceTime : 0) + cycle).toISOString();
    const details = { ...(original.details ?? {}) };
    if (index === count - 1) {
      details['performancePayload'] = `TIMELINE-PAYLOAD-MARKER\n${'large payload line\n'.repeat(250)}`;
    }
    return {
      ...original,
      ts,
      runId: `${original.runId ?? 'event'}-perf-${index}`,
      payloadRef: original.payloadRef ? `${original.payloadRef}#perf-${index}` : null,
      summary: `${original.summary} [perf ${index}]`,
      details,
    };
  });
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installTimelineRoutes(
  page: Page,
  scenario: Scenario,
  detail: Record<string, unknown>,
  events: readonly TimelineEvent[],
): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const taskSuffixes = [encodeURIComponent(scenario.key), encodeURIComponent(scenario.id)];
    const taskInfo = { ...((detail['info'] as Record<string, unknown>) ?? {}), state: '6-completed' };
    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (/\/api\/cli\/[^/]+\/models$/.test(pathname)) {
      return json(route, { models: [], source: 'timeline-performance-e2e' });
    }
    if (pathname === '/api/watch-paths') {
      return json(route, [{ name: scenario.key, path: scenario.watchPath, rootPath: scenario.watchPath }]);
    }
    if (pathname === '/api/tasks/grouped') {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
        escalated: [], completed: [taskInfo], archive: [],
      });
    }
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (taskSuffixes.some(suffix => pathname === `/api/tasks/${suffix}/timeline`)) {
      await page.evaluate(() => performance.mark('timeline-response-start')).catch(() => undefined);
      return json(route, events);
    }
    if (taskSuffixes.some(suffix => pathname === `/api/tasks/${suffix}/runs`)) {
      return json(route, {
        runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [],
      });
    }
    if (taskSuffixes.some(suffix => pathname === `/api/tasks/${suffix}`)) {
      return json(route, { ...detail, info: taskInfo });
    }
    if (pathname === '/api/runner/status') return json(route, { projects: {} });
    if (pathname === '/api/runner/global') return json(route, { mode: 'paused', activeProjects: [] });
    if (pathname === '/api/runner/queue-starvation') {
      return json(route, {
        active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30,
        oldestEnteredLaneAt: null, observedAt: new Date().toISOString(), items: [],
      });
    }
    if (pathname === '/api/pipeline/accepted-integration-alert') {
      return json(route, {
        active: false, stalledTaskCount: 0, thresholdMinutes: 30,
        oldestAcceptedAt: null, observedAt: new Date().toISOString(), items: [],
      });
    }
    if (pathname === '/api/crash-recovery/pending') return json(route, { pending: [] });
    if (pathname === '/api/cli/quota') return json(route, { snapshots: [], ttlSeconds: 600 });
    if (pathname === '/api/cli/usage') return json(route, { items: [] });
    if (pathname === '/api/environment') {
      return json(route, { isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } });
    }
    if (pathname === '/api/tasks' || pathname === '/api/projects' || pathname === '/api/workspaces'
      || pathname === '/api/tags' || pathname === '/api/clients' || pathname === '/api/clients/'
      || pathname === '/api/agent-rules' || pathname === '/api/epics' || pathname === '/api/git/summary'
      || pathname === '/api/v1/management/remote-hosts') {
      return json(route, []);
    }
    if (pathname === '/api/epics/completed/count') return json(route, { count: 0 });
    if (pathname.startsWith('/api/runner/token-summary-aggregate')) {
      return json(route, {
        projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
        totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0, estimatedApiCostUsd: 0, allModelsPriced: true,
        byModel: [], byProject: [], fetchedAt: new Date().toISOString(), disclaimer: '',
      });
    }
    if (pathname.startsWith('/api/workspace/tokens/timeline')) {
      return json(route, {
        windowStart: new Date().toISOString(), windowEnd: new Date().toISOString(),
        windowHours: 24, bucketMinutes: 60, bucketCount: 0, cells: [], projects: [],
        fetchedAt: new Date().toISOString(), disclaimer: '',
      });
    }
    if (pathname.startsWith('/api/workspace/tokens/expensive-jobs')) return json(route, { jobs: [] });
    if (/\/api\/bus\/[^/]+\/messages$/.test(pathname)) return json(route, []);
    if (pathname.includes('/screenshots')) return json(route, { screenshots: [] });
    if (pathname.includes('/pipeline')) return json(route, null);
    if (pathname.includes('/session-events')) return json(route, { events: [], sessionChain: [] });
    if (pathname.includes('/claude-session')) return json(route, null);
    if (pathname.includes('/agent-work-summary')) {
      return json(route, {
        calls: 0, recovered: false, toolCalls: 0, toolCounts: [],
        startedAt: null, lastTouchAt: null, currentSessionId: null,
      });
    }
    if (pathname.includes('/plan')) {
      return json(route, {
        hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
        softEstimateMedian: null, items: [], unassignedSubActions: [],
      });
    }
    if (pathname.includes('/output')) return json(route, []);
    return json(route, request.method() === 'GET' ? {} : {});
  });
}

async function afterPaint(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>(resolve => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
}

async function browserMetrics(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('Performance.enable');
  await cdp.send('HeapProfiler.collectGarbage');
  const result = await cdp.send('Performance.getMetrics');
  const metric = (name: string) => result.metrics.find(item => item.name === name)?.value ?? 0;
  return {
    jsHeapUsedBytes: metric('JSHeapUsedSize'),
    jsHeapTotalBytes: metric('JSHeapTotalSize'),
    cdpNodes: metric('Nodes'),
    cdpDocuments: metric('Documents'),
  };
}

async function measureFrameRates(page: Page): Promise<{
  availableFrameFps: number;
  scrollFps: number;
}> {
  return await page.evaluate(async () => {
    const list = document.querySelector('[data-testid="timeline-list"]');
    if (!list) return { availableFrameFps: 0, scrollFps: 0 };
    let scroller: HTMLElement | null = list.parentElement;
    while (scroller && scroller.scrollHeight <= scroller.clientHeight) scroller = scroller.parentElement;
    if (!scroller) return { availableFrameFps: 0, scrollFps: 0 };
    scroller.scrollTop = 0;
    const maxScroll = Math.max(1, scroller.scrollHeight - scroller.clientHeight);
    const samples = await new Promise<{ idle: number[]; scroll: number[] }>(resolve => {
      const idle: number[] = [];
      const scroll: number[] = [];
      let previousAction: 'idle' | 'scroll' = 'idle';
      let previousTimestamp = performance.now();
      let frame = 0;
      const started = previousTimestamp;
      const tick = (timestamp: number) => {
        const duration = timestamp - previousTimestamp;
        if (duration > 0 && duration < 250) {
          (previousAction === 'scroll' ? scroll : idle).push(duration);
        }
        if (timestamp - started >= 4_000) {
          resolve({ idle, scroll });
          return;
        }
        previousAction = frame % 2 === 0 ? 'scroll' : 'idle';
        if (previousAction === 'scroll') {
          scroller!.scrollTop = (scroller!.scrollTop + 48) % maxScroll;
        }
        previousTimestamp = timestamp;
        frame += 1;
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
    const trimmedMean = (values: number[]) => {
      const ordered = [...values].sort((a, b) => a - b);
      const trim = Math.floor(ordered.length * 0.1);
      const kept = ordered.slice(trim, Math.max(trim + 1, ordered.length - trim));
      return kept.reduce((sum, value) => sum + value, 0) / Math.max(1, kept.length);
    };
    return {
      availableFrameFps: 1_000 / trimmedMean(samples.idle),
      scrollFps: 1_000 / trimmedMean(samples.scroll),
    };
  });
}

test.describe('Task timeline performance at real-payload scale', () => {
  test.skip(!ENABLED, 'Set RUN_TIMELINE_PERF=1 to capture task-timeline performance evidence.');

  for (const scenario of SCENARIOS) {
    test(`${scenario.key}: 1,000 event render, memory, payload and scroll`, async ({ page, context }) => {
      test.setTimeout(180_000);
      const fixture = await loadScenario(scenario);
      await installTimelineRoutes(page, scenario, fixture.detail, fixture.events);
      await page.setViewportSize({ width: 1440, height: 1_000 });

      const navigationStarted = Date.now();
      await page.goto(`/#/tasks/${scenario.key}?view=timeline%3Aprotocol`, { waitUntil: 'commit' });
      await expect(page.getByTestId('prompt-tab-timeline'))
        .toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
      const renderedRows = EXPECT_WINDOWED ? WINDOW_COUNT : TARGET_COUNT;
      await expect(page.getByTestId('timeline-event')).toHaveCount(renderedRows, { timeout: 30_000 });
      await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
      await afterPaint(page);

      const responseToRenderMs = await page.evaluate(() => {
        const mark = performance.getEntriesByName('timeline-response-start').at(-1)?.startTime ?? 0;
        return performance.now() - mark;
      });
      const initialRenderMs = Date.now() - navigationStarted;
      const domNodes = await page.evaluate(() => document.querySelectorAll('*').length);
      const timelineDomNodes = await page.evaluate(() =>
        document.querySelector('[data-testid="timeline-tab"]')?.querySelectorAll('*').length ?? 0);
      const memory = await browserMetrics(context, page);
      await page.waitForTimeout(1_000);
      const { availableFrameFps, scrollFps } = await measureFrameRates(page);
      const normalizedScrollFps = Math.min(60, scrollFps / Math.max(1, availableFrameFps) * 60);
      const payloadInDomBeforeExpand = await page.evaluate(() =>
        document.body.textContent?.includes('TIMELINE-PAYLOAD-MARKER') ? 1 : 0);

      const evidence = {
        phase: PHASE,
        scenario: scenario.key,
        sourceEventCount: fixture.sourceEvents.length,
        sourceJsonBytes: Buffer.byteLength(JSON.stringify(fixture.sourceEvents)),
        scaledEventCount: TARGET_COUNT,
        renderedRows,
        initialRenderMs,
        responseToRenderMs,
        availableFrameFps,
        scrollFps,
        normalizedScrollFps,
        domNodes,
        timelineDomNodes,
        payloadInDomBeforeExpand,
        ...memory,
        measuredAt: new Date().toISOString(),
      };
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      fs.writeFileSync(
        path.join(RESULTS_DIR, `timeline-performance-${PHASE}-${scenario.key.toLowerCase()}.json`),
        `${JSON.stringify(evidence, null, 2)}\n`,
      );

      if (PHASE === 'after' && scenario.key === 'AGT-2577') {
        await dismissDevErrorDialog(page);
        await page.addStyleTag({ content: '[role="alert"] { display: none !important; }' });
        await page.getByTestId('timeline-load-older').scrollIntoViewIfNeeded();
        await setTheme(page, 'light');
        await page.screenshot({
          path: path.join(RESULTS_DIR, 'timeline-windowing--light--mocked.png'), fullPage: false,
        });
        await setTheme(page, 'dark');
        await page.screenshot({
          path: path.join(RESULTS_DIR, 'timeline-windowing--dark--mocked.png'), fullPage: false,
        });
      }

      if (EXPECT_WINDOWED) {
        expect(payloadInDomBeforeExpand).toBe(0);
        await expect(page.getByTestId('timeline-load-older')).toContainText('950 older events');
        expect(responseToRenderMs).toBeLessThanOrEqual(1_000);
        expect(normalizedScrollFps).toBeGreaterThanOrEqual(55);
        expect(timelineDomNodes).toBeLessThanOrEqual(3_000);
        const lastRow = page.getByTestId('timeline-event').last();
        const payload = lastRow.getByTestId('timeline-event-payload');
        await payload.click();
        await expect(lastRow.getByTestId('timeline-event-payload-content'))
          .toContainText('TIMELINE-PAYLOAD-MARKER');
        await payload.click();
        await expect(lastRow.getByTestId('timeline-event-payload-content')).toHaveCount(0);
      } else {
        expect(payloadInDomBeforeExpand).toBeGreaterThan(0);
      }

      console.log(JSON.stringify(evidence));
    });
  }
});
