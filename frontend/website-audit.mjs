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

function slug(r) {
  return r === '/' ? 'home' : r.slice(1).replace(/\//g, '_');
}

const report = [];

const browser = await chromium.launch();
for (const vp of viewports) {
  const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
  const page = await ctx.newPage();
  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', (e) => consoleErrors.push('PAGEERROR: ' + e.message));

  for (const route of routes) {
    consoleErrors.length = 0;
    const entry = { route, viewport: vp.name, issues: [] };
    try {
      const resp = await page.goto(BASE + route, { waitUntil: 'networkidle', timeout: 30000 });
      entry.status = resp ? resp.status() : 'n/a';
      await page.waitForTimeout(600);

      // full-page screenshot
      const file = `${OUT}/${slug(route)}--${vp.name}.png`;
      await page.screenshot({ path: file, fullPage: true });
      entry.screenshot = file;

      // metrics
      const m = await page.evaluate(() => {
        const doc = document.documentElement;
        const body = document.body;
        const hOverflow = Math.max(doc.scrollWidth, body.scrollWidth) - doc.clientWidth;

        // find elements wider than viewport
        const wide = [];
        const all = document.querySelectorAll('body *');
        for (const el of all) {
          const r = el.getBoundingClientRect();
          if (r.width > 0 && (r.right > doc.clientWidth + 2 || r.left < -2)) {
            const cs = getComputedStyle(el);
            if (cs.position === 'fixed' && cs.transform !== 'none') continue;
            let sel = el.tagName.toLowerCase();
            if (el.id) sel += '#' + el.id;
            else if (el.className && typeof el.className === 'string') sel += '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.');
            wide.push({ sel, left: Math.round(r.left), right: Math.round(r.right), w: Math.round(r.width) });
            if (wide.length >= 15) break;
          }
        }

        // broken images
        const badImgs = [];
        for (const img of document.querySelectorAll('img')) {
          if (!img.complete || img.naturalWidth === 0) {
            badImgs.push({ src: img.getAttribute('src'), alt: img.alt?.slice(0, 60) });
          }
        }

        // page height
        const pageHeight = Math.max(doc.scrollHeight, body.scrollHeight);

        return { hOverflow, clientWidth: doc.clientWidth, wide, badImgs, pageHeight, title: document.title };
      });
      entry.metrics = m;
      if (m.hOverflow > 2) entry.issues.push(`HORIZONTAL OVERFLOW: ${m.hOverflow}px beyond viewport`);
      if (m.badImgs.length) entry.issues.push(`BROKEN IMAGES: ${JSON.stringify(m.badImgs)}`);
      if (m.wide.length) entry.issues.push(`WIDE ELEMENTS: ${JSON.stringify(m.wide.slice(0, 8))}`);
      if (consoleErrors.length) entry.issues.push(`CONSOLE: ${consoleErrors.slice(0, 5).join(' | ')}`);
    } catch (e) {
      entry.error = e.message;
    }
    report.push(entry);
    console.log(`${vp.name} ${route} -> ${entry.issues.length} issue groups${entry.error ? ' ERROR ' + entry.error : ''}`);
  }
  await ctx.close();
}
await browser.close();

fs.writeFileSync(`${OUT}/report.json`, JSON.stringify(report, null, 2));
console.log('DONE');
