import { expect, test } from '@playwright/test';

const evidenceDir = process.env.UPDATE_CENTER_RESULTS_DIR;

function documentFor(theme: 'dark' | 'light'): string {
  return `<!doctype html><html data-studio-theme="${theme}"><head><style>
    :root { --bg:#11111b; --surface:#181825; --card:#1e1e2e; --fg:#cdd6f4; --strong:#f5e0dc; --dim:#a6adc8; --border:#45475a; --green:#a6e3a1; --yellow:#f9e2af; --hash:#a6e3a1; }
    html[data-studio-theme=light] { --bg:#f8fafc; --surface:#fff; --card:#f1f5f9; --fg:#334155; --strong:#0f172a; --dim:#64748b; --border:#cbd5e1; --green:#15803d; --yellow:#a16207; --hash:#166534; }
    * { box-sizing:border-box } body { margin:0; background:var(--bg); color:var(--fg); font:14px system-ui,sans-serif }
    .center { width:560px; min-height:100vh; margin-left:auto; padding:24px; background:var(--surface); border-left:1px solid var(--border); display:flex; flex-direction:column; gap:24px }
    header,.meta,.branch-head { display:flex; align-items:center; justify-content:space-between } h1 { margin:0; color:var(--strong); font-size:22px } p,small { color:var(--dim) }
    .hero { display:grid; gap:12px; padding:24px; border:1px solid color-mix(in srgb,var(--green) 42%,var(--border)); border-radius:16px; background:color-mix(in srgb,var(--green) 8%,var(--card)) }
    .eyebrow { color:var(--green); font-size:12px; font-weight:700; letter-spacing:.06em; text-transform:uppercase }.dot { display:inline-block;width:8px;height:8px;border-radius:50%;background:var(--green);margin-right:8px }
    .version { color:var(--strong); font:700 23px ui-monospace,monospace }.meta { justify-content:flex-start;gap:12px;color:var(--dim);font-size:13px } code { color:var(--hash) }
    .pill { width:max-content;padding:4px 8px;border-radius:999px;background:color-mix(in srgb,var(--yellow) 14%,transparent);color:var(--yellow);font-size:12px;font-weight:700 }
    .branches { display:grid;grid-template-columns:1fr 1fr;gap:12px }.branch { display:grid;gap:8px;padding:16px;border:1px solid var(--border);border-radius:12px;background:var(--card) }.branch-head span { color:var(--strong);font-weight:700 }.branch strong { font-size:13px }
    h2 { margin:0 0 12px;color:var(--dim);font-size:11px;letter-spacing:.08em;text-transform:uppercase }.commit { display:grid;grid-template-columns:auto 1fr;gap:12px;padding:12px;border-radius:10px;background:var(--card) }
  </style></head><body><aside class="center" data-testid="update-center">
    <header><div><h1>Update Center</h1><p>Runtime truth, release branches and deploy history in one place.</p></div><span>↻ &nbsp; ×</span></header>
    <section class="hero" data-testid="update-center-runtime"><div class="eyebrow"><span class="dot"></span>Running Agent Studio</div><strong class="version">2026.07.10-1000+a1b2c3d</strong><div class="meta"><code>a1b2c3d</code><span>Deployed Jul 10, 2026, 12:00</span></div><span class="pill">Update available</span></section>
    <section class="branches"><article class="branch" data-testid="update-center-main-version"><div class="branch-head"><span>main</span><code>d4e5f6a</code></div><strong>3 ahead of running</strong><small>Jul 11, 2026, 14:30</small></article><article class="branch" data-testid="update-center-develop-version"><div class="branch-head"><span>develop</span><code>f7a8b9c</code></div><strong>7 ahead of running</strong><small>Jul 11, 2026, 14:45</small></article></section>
    <section><h2>What changes with the next deploy</h2><div class="commit"><code>d4e5f6a</code><span>Polish workspace navigation</span></div><div class="commit"><code>c3d4e5f</code><span>Harden deploy verification</span></div></section>
  </aside></body></html>`;
}

for (const theme of ['dark', 'light'] as const) {
  test(`shows running, main and develop truth in ${theme} theme`, async ({ page }) => {
    await page.setContent(documentFor(theme));
    await expect(page.getByTestId('update-center-runtime')).toContainText('2026.07.10-1000+a1b2c3d');
    await expect(page.getByTestId('update-center-main-version')).toContainText('3 ahead of running');
    await expect(page.getByTestId('update-center-develop-version')).toContainText('7 ahead of running');
    if (evidenceDir) await page.getByTestId('update-center').screenshot({ path: `${evidenceDir}/update-center-${theme}--mocked.png` });
  });
}
