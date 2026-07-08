import { chromium } from 'playwright';

const BASE = 'http://127.0.0.1:4184';
const OUT = 'C:/Users/rmisc/AppData/Local/Temp/claude/c--Projects-agent-taskboard-devspace/f2bd6a6a-eb7e-498d-9281-e211af477922/scratchpad/website-audit';

const targets = [
  { route: '/', name: 'home' },
  { route: '/product', name: 'product' },
  { route: '/compared-to-other-products', name: 'compare' },
  { route: '/tokens', name: 'tokens' },
  { route: '/about', name: 'about' },
  { route: '/hire', name: 'hire' },
  { route: '/imprint', name: 'imprint' },
  { route: '/patterns', name: 'patterns' },
  { route: '/articles/specs-are-context-not-control', name: 'article' },
];

const viewports = [
  { name: 'desktop', width: 1600, height: 1000 },
  { name: 'mobile', width: 390, height: 844 }
];

const selectors = [
  ['hero', 'header.hero'],
  ['video', 'app-marketing-video'],
  ['proof', 'app-product-proof'],
  ['gallery', 'app-product-visual-gallery'],
  ['pipeline', 'figure.section-pipeline'],
  ['footer', 'footer.site-footer'],
  ['catalog-header', 'header.catalog-header'],
  ['catalog-list', 'section.catalog-list'],
  ['comparison-header', 'header.comparison-header'],
  ['comparison-market', 'section.comparison-market'],
  ['comparison-table', 'section.comparison-table'],
  ['hire-options', 'section.hire-options'],
  ['essay-hero', 'header.essay-hero'],
  ['essay-body', 'div.essay-body'],
];

const browser = await chromium.launch();
for (const vp of viewports) {
  const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
  const page = await ctx.newPage();
  for (const t of targets) {
    await page.goto(BASE + t.route, { waitUntil: 'networkidle', timeout: 30000 });
    // scroll to trigger defer/lazy
    await page.evaluate(async () => {
      const max = () => Math.max(document.documentElement.scrollHeight, document.body.scrollHeight);
      for (let y = 0; y < max(); y += window.innerHeight * 0.8) {
        window.scrollTo(0, y);
        await new Promise((r) => setTimeout(r, 100));
      }
      window.scrollTo(0, 0);
      await new Promise((r) => setTimeout(r, 300));
    });
    await page.waitForTimeout(300);

    for (const [label, sel] of selectors) {
      const loc = page.locator(sel).first();
      if (await loc.count() === 0) continue;
      try {
        await loc.scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await loc.screenshot({ path: `${OUT}/el-${t.name}-${label}--${vp.name}.png`, timeout: 8000 });
      } catch (e) { console.log('skip', t.name, label, vp.name, e.message.split('\n')[0]); }
    }

    // content sections by id
    const ids = await page.evaluate(() =>
      [...document.querySelectorAll('section.content-section')].map((s) => s.id)
    );
    for (const id of ids) {
      const loc = page.locator(`section#${id}`).first();
      try {
        await loc.scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await loc.screenshot({ path: `${OUT}/el-${t.name}-sec-${id}--${vp.name}.png`, timeout: 8000 });
      } catch (e) { console.log('skip', t.name, id, vp.name, e.message.split('\n')[0]); }
    }
    console.log(vp.name, t.name, 'done, sections:', ids.join(','));
  }
  await ctx.close();
}
await browser.close();
console.log('DONE3');
