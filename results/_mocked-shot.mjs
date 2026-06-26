// Disposable evidence-screenshot driver for T7a. Renders THIS worktree's
// production build (served by _static-server.mjs) with the CLI admin API
// surface route-mocked, opens the Workspace Settings "Usage caps" overlay,
// and captures the models / completion-contracts / working-memory sections.
//
// This is a --mocked shot: a --real shot is not reachable from a dev job
// worktree because the running dev stack (:4010/:5030) serves the canonical
// dev checkout, not this branch, and AGENTS.md forbids bringing the dev
// backend up from a job. The committed real-backend regression spec
// (frontend/e2e/cli/cli-admin-models-contracts.spec.ts) is what the stable
// seat runs for the --real shot.
import { chromium } from 'playwright';

const BASE = process.env.SHOT_BASE || 'http://127.0.0.1:4099';
const OUT = process.argv[2] || 'results/cli-admin-types-models-contracts--mocked.png';

const CONTRACTS = [
  {
    cliType: 'claude', transport: 'stream-json NDJSON',
    sessionStartSignal: 'system frame, subtype=init',
    completionSignal: 'result frame, is_error=false',
    failureSignal: 'result frame, is_error=true',
    usageSource: 'result.usage', typed: true,
    notes: 'ClaudeEventAdapter maps native frames to CliRunEvent.',
  },
  {
    cliType: 'codex', transport: 'JSONL (codex exec --json)',
    sessionStartSignal: 'thread.started / session_meta',
    completionSignal: 'turn.completed',
    failureSignal: 'turn.failed',
    usageSource: 'turn.completed.usage', typed: true,
    notes: 'CodexEventAdapter maps native frames to CliRunEvent.',
  },
  {
    cliType: 'gemini', transport: 'stream-json NDJSON',
    sessionStartSignal: 'init',
    completionSignal: 'result frame, status=success',
    failureSignal: 'result frame, status!=success',
    usageSource: 'result.stats', typed: true,
    notes: 'GeminiEventAdapter maps native frames to CliRunEvent.',
  },
  {
    cliType: 'copilot', transport: 'PTY / TUI (no structured stream)',
    sessionStartSignal: 'n/a',
    completionSignal: 'process exit (heuristic, exit-based)',
    failureSignal: 'non-zero exit (heuristic)',
    usageSource: 'n/a', typed: false,
    notes: 'No typed adapter; completion is inferred from process exit.',
  },
];

function catalogFor(type) {
  const m = {
    claude: { id: 'claude-opus-4-7', label: 'Claude Opus 4.7', vendor: 'Anthropic',
      thinkingLevels: ['none', 'think', 'ultrathink'], defaultThinkingLevel: 'think' },
    codex: { id: 'gpt-5-codex', label: 'GPT-5 Codex', vendor: 'OpenAI',
      thinkingLevels: ['low', 'medium', 'high'], defaultThinkingLevel: 'medium' },
    gemini: { id: 'gemini-2.5-pro', label: 'Gemini 2.5 Pro', vendor: 'Google',
      thinkingLevels: [], defaultThinkingLevel: null },
    copilot: { id: 'gpt-4.1', label: 'GPT-4.1', vendor: 'GitHub',
      thinkingLevels: [], defaultThinkingLevel: null },
  }[type];
  const models = m
    ? [{ id: m.id, label: m.label, multiplier: 1, vendor: m.vendor, isDefault: true,
        thinkingLevels: m.thinkingLevels, defaultThinkingLevel: m.defaultThinkingLevel,
        available: true, deprecated: false, availabilityNote: null }]
    : [];
  return { models, source: 'mocked-evidence', fetchedAt: new Date().toISOString() };
}

const QUOTA = {
  at: new Date().toISOString(),
  ttlSeconds: 600,
  snapshots: [
    { cliType: 'claude', fetchedAt: new Date().toISOString(), plan: 'Max', source: 'mocked',
      rawSample: null, error: null,
      windows: [{ label: '5h', usedPct: 42, used: 42, limit: 100, unit: '%', resetAt: null, resetLabel: '5h window' }] },
    { cliType: 'codex', fetchedAt: new Date().toISOString(), plan: 'Plus', source: 'mocked',
      rawSample: null, error: null,
      windows: [{ label: 'weekly', usedPct: 18, used: 18, limit: 100, unit: '%', resetAt: null, resetLabel: 'weekly' }] },
  ],
};

const json = (route, body) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

async function main() {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1320, height: 1600 } });

  await page.route('**/api/**', async (route) => {
    const url = route.request().url();
    if (url.includes('/api/cli/contracts')) return json(route, CONTRACTS);
    const mm = url.match(/\/api\/cli\/(claude|codex|gemini|copilot)\/models/);
    if (mm) return json(route, catalogFor(mm[1]));
    if (url.includes('/api/cli/quota/caps')) return json(route, { defaultCapPct: 95, caps: {} });
    if (url.includes('/api/cli/quota')) return json(route, QUOTA);
    if (url.includes('/api/cli/usage')) return json(route, { at: new Date().toISOString(), sections: [] });
    // Everything else (shell boot graph + usage-detail polls): fail like an
    // offline backend. Services take their error path and keep safe empty
    // defaults; returning a wrong-shaped [] instead trips `.length` reads in
    // boot computeds and wedges the shell so the settings modal never opens.
    return route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"mock offline"}' });
  });

  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console.error: ' + m.text().slice(0, 200)); });
  const seen = new Set();
  page.on('request', (r) => { const u = r.url(); if (u.includes('/api/')) seen.add(u.replace(BASE, '')); });

  await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);

  // Empty-list mocks for boot endpoints can trip a global error dialog that
  // overlays the shell. Capture its message once for diagnostics, then clear
  // any open dialog via backdrop click so the status bar is interactable.
  const dismissDialogs = async () => {
    for (let i = 0; i < 8; i++) {
      const overlayEl = page.locator('.dialog__overlay');
      if ((await overlayEl.count()) === 0) return;
      const msg = page.getByTestId('error-dialog-message');
      if ((await msg.count()) > 0) {
        console.log('error dialog message:', (await msg.first().innerText()).slice(0, 240));
      }
      await overlayEl.first().click({ position: { x: 4, y: 4 }, timeout: 2000 }).catch(() => {});
      await page.keyboard.press('Escape').catch(() => {});
      await page.waitForTimeout(300);
    }
  };
  await dismissDialogs();

  try {
    await page.getByTestId('status-bar-settings').click({ timeout: 15000 });
    await dismissDialogs();
    await page.getByTestId('workspace-settings-rail-caps').click({ timeout: 15000 });

    const overlay = page.getByTestId('cli-admin-overlay');
    await overlay.waitFor({ state: 'visible', timeout: 20000 });
    await page.getByTestId('cli-admin-contracts').waitFor({ state: 'visible', timeout: 20000 });
    await page.getByTestId('cli-admin-models').waitFor({ state: 'visible', timeout: 20000 });
    await page.getByTestId('cli-admin-working-memory').waitFor({ state: 'visible', timeout: 20000 });
    // Let lazy content settle before the shot.
    await page.waitForTimeout(800);

    // Full overlay (all sections) as a record.
    await page.getByTestId('cli-admin-models').scrollIntoViewIfNeeded().catch(() => {});
    await page.waitForTimeout(150);

    // Focused T7a evidence: collapse the non-T7a sections (usage caps, usage
    // detail, CLI sessions) so the single image shows exactly what T7a adds —
    // CLI types & models, completion contracts, and the working-memory
    // placeholder — without the empty usage-detail area dominating the frame.
    await page.addStyleTag({
      content: `
        .dialog__overlay { display: none !important; }
        app-cli-admin-panel .cli-admin__section { display: none !important; }
        app-cli-admin-panel [data-testid="cli-admin-models"],
        app-cli-admin-panel [data-testid="cli-admin-contracts"],
        app-cli-admin-panel [data-testid="cli-admin-working-memory"] { display: block !important; }
      `,
    });
    await dismissDialogs();
    await page.waitForTimeout(200);

    // Clip from the panel top to the bottom of the contracts section so the
    // primary evidence image is exactly: header + CLI types & models +
    // completion contracts, with no modal backdrop-filter bleed below it.
    const panelBox = await page.getByTestId('cli-admin-panel').boundingBox();
    const contractsBox = await page.getByTestId('cli-admin-contracts').boundingBox();
    if (panelBox && contractsBox) {
      const clip = {
        x: Math.max(0, Math.floor(panelBox.x)),
        y: Math.max(0, Math.floor(panelBox.y)),
        width: Math.ceil(panelBox.width),
        height: Math.ceil(contractsBox.y + contractsBox.height - panelBox.y),
      };
      await page.screenshot({ path: OUT, clip });
    } else {
      await page.getByTestId('cli-admin-panel').screenshot({ path: OUT });
    }
    console.log(`screenshot written: ${OUT}`);

    // Second evidence shot: the "Working memory" placeholder (T7a ships it as a
    // labelled coming-soon section until T1c lands its backing data).
    const wm = page.getByTestId('cli-admin-working-memory');
    await wm.scrollIntoViewIfNeeded().catch(() => {});
    await dismissDialogs();
    await page.waitForTimeout(150);
    await wm.screenshot({ path: '../results/cli-admin-working-memory--mocked.png' }).catch((e) => console.log('wm shot skipped:', e.message));
  } catch (err) {
    console.log('--- DIAGNOSTICS ---');
    console.log('settingsOpen testid present:', (await page.getByTestId('workspace-settings-overlay').count()));
    console.log('/api requests seen:\n  ' + [...seen].sort().join('\n  '));
    console.log('errors:\n  ' + (errors.join('\n  ') || '(none)'));
    await page.screenshot({ path: '../results/_debug-fullpage.png', fullPage: true }).catch(() => {});
    throw err;
  } finally {
    await browser.close();
  }
}

main().catch(async (e) => {
  console.error('SHOT FAILED:', e.message);
  process.exit(1);
});
