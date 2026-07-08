import { chromium } from 'playwright';
import fs from 'fs';

const BASE = 'http://127.0.0.1:4184';
const OUT = 'C:/Users/rmisc/AppData/Local/Temp/claude/c--Projects-agent-taskboard-devspace/f2bd6a6a-eb7e-498d-9281-e211af477922/scratchpad/website-audit';

const routes = [
  '/', '/news', '/open-source', '/product', '/compared-to-other-products',
  '/context-management', '/software-quality', '/security', '/tokens',
  '/patterns', '/patterns/external-cli-on-the-side', '/articles',
  '/patterns/workforce-sensemaking-cli', '/patterns/plus-one-developer',
  '/patterns/angular-quality-rails', '/articles/specs-are-context-not-control',
  '/articles/workforce-sensemaking-cli', '/about', '/hire', '/imprint'
];

const viewports = [
  { name: 'desktop', width: 1600, height: 1000 },
  { name: 'tablet', width: 900, height: 1200 },
  { name: 'mobile', width: 390, height: 844 }
];

function slug(r) { return r === '/' ? 'home' : r.slice(1).replace(/\//g, '_'); }

async function fullScroll(page) {
  await page.evaluate(async () => {
    const step = window.innerHeight * 0.8;
    let y = 0;
    const max = () => Math.max(document.documentElement.scrollHeight, document.body.scrollHeight);
    while (y < max()) {
      window.scrollTo(0, y);
      await new Promise((r) => setTimeout(r, 120));
      y += step;
    }
    window.scrollTo(0, max());
    await new Promise((r) => setTimeout(r, 400));
    window.scrollTo(0, 0);
    await new Promise((r) => setTimeout(r, 300));
  });
}

const report = [];
const browser = await chromium.launch();

for (const vp of viewports) {
  const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
  const page = await ctx.newPage();
  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', (e) => consoleErrors.push('PAGEERROR: ' + e.message));
  const failedReqs = [];
  page.on('requestfailed', (r) => failedReqs.push(r.url()));
  page.on('response', (r) => { if (r.status() >= 400) failedReqs.push(`${r.status()} ${r.url()}`); });

  for (const route of routes) {
    consoleErrors.length = 0; failedReqs.length = 0;
    const entry = { route, viewport: vp.name, issues: [] };
    try {
      await page.goto(BASE + route, { waitUntil: 'networkidle', timeout: 30000 });
      await fullScroll(page);
      await page.waitForTimeout(400);

      const file = `${OUT}/v2-${slug(route)}--${vp.name}.png`;
      await page.screenshot({ path: file, fullPage: true });
      entry.screenshot = file;

      const m = await page.evaluate(() => {
        const doc = document.documentElement;
        const hOverflow = Math.max(doc.scrollWidth, document.body.scrollWidth) - doc.clientWidth;

        const badImgs = [];
        for (const img of document.querySelectorAll('img')) {
          if (!img.complete || img.naturalWidth === 0) badImgs.push(img.getAttribute('src'));
        }

        // text overlap detection: compare sibling-ish leaf text blocks
        const overlaps = [];
        const leaves = [...document.querySelectorAll('main h1, main h2, main h3, main h4, main p, main li, main a.btn, main span, main b, main strong, main td, main th')]
          .filter((el) => el.offsetParent !== null && el.textContent.trim().length > 2)
          .map((el) => ({ el, r: el.getBoundingClientRect() }))
          .filter((x) => x.r.width > 4 && x.r.height > 4);
        for (let i = 0; i < leaves.length && overlaps.length < 12; i++) {
          for (let j = i + 1; j < leaves.length && overlaps.length < 12; j++) {
            const a = leaves[i], b = leaves[j];
            if (a.el.contains(b.el) || b.el.contains(a.el)) continue;
            const ix = Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left);
            const iy = Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top);
            if (ix > 6 && iy > 6) {
              const area = ix * iy;
              const minArea = Math.min(a.r.width * a.r.height, b.r.width * b.r.height);
              if (area > minArea * 0.35) {
                const d = (x) => {
                  let s = x.el.tagName.toLowerCase();
                  if (x.el.className && typeof x.el.className === 'string') s += '.' + x.el.className.trim().split(/\s+/)[0];
                  return s + ' "' + x.el.textContent.trim().slice(0, 40) + '"';
                };
                overlaps.push(d(a) + ' <-> ' + d(b));
              }
            }
          }
        }

        // tiny-font / low readability check
        return { hOverflow, badImgs, overlaps };
      });
      if (m.hOverflow > 2) entry.issues.push(`H-OVERFLOW ${m.hOverflow}px`);
      if (m.badImgs.length) entry.issues.push(`BROKEN-IMG ${m.badImgs.join(', ')}`);
      if (m.overlaps.length) entry.issues.push(`OVERLAP ${m.overlaps.join(' || ')}`);
      if (consoleErrors.length) entry.issues.push(`CONSOLE ${consoleErrors.slice(0, 3).join(' | ')}`);
      if (failedReqs.length) entry.issues.push(`REQFAIL ${[...new Set(failedReqs)].slice(0, 5).join(', ')}`);
    } catch (e) { entry.error = e.message; }
    report.push(entry);
    console.log(`${vp.name} ${route} -> ${entry.issues.length ? JSON.stringify(entry.issues) : 'ok'}${entry.error ? ' ERR ' + entry.error : ''}`);
  }

  // hero close-ups for key pages (viewport-sized, not full page)
  for (const route of ['/', '/product', '/compared-to-other-products', '/about', '/tokens', '/hire']) {
    try {
      await page.goto(BASE + route, { waitUntil: 'networkidle', timeout: 30000 });
      await page.waitForTimeout(500);
      await page.screenshot({ path: `${OUT}/hero-${slug(route)}--${vp.name}.png`, fullPage: false });
    } catch {}
  }

  // open mobile drawer state
  if (vp.name !== 'desktop') {
    try {
      await page.goto(BASE + '/', { waitUntil: 'networkidle', timeout: 30000 });
      const toggle = page.locator('button.menu-toggle');
      if (await toggle.isVisible()) {
        await toggle.click();
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${OUT}/drawer-open--${vp.name}.png`, fullPage: false });
      } else {
        console.log(vp.name + ': menu-toggle not visible');
      }
    } catch (e) { console.log('drawer error', e.message); }
  }

  await ctx.close();
}
await browser.close();
fs.writeFileSync(`${OUT}/report2.json`, JSON.stringify(report, null, 2));
console.log('DONE2');
