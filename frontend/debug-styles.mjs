import { chromium } from '@playwright/test';
const BASE = 'http://localhost:4012';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
await page.addInitScript(() => {
  try { localStorage.setItem('atp.flag.vsCodeLayout', '1'); localStorage.setItem('atp.theme', 'dark'); } catch {}
});
await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
const welcome = page.getByTestId('studio-welcome');
if ((await welcome.count()) > 0 && await welcome.first().isVisible().catch(()=>false)) {
  await welcome.first().getByRole('button', { name: 'All projects' }).click().catch(()=>{});
}
await page.getByTestId('studio-board').first().waitFor({ state: 'visible', timeout: 15000 }).catch(()=>{});
const epic = page.getByTestId('studio-board-epic-toggle');
if ((await epic.getAttribute('aria-pressed')) === 'false') await epic.click();
await page.waitForTimeout(200);
const info = await epic.evaluate((n) => {
  const s = getComputedStyle(n);
  const root = getComputedStyle(document.documentElement);
  const host = document.querySelector('app-studio-shell');
  const hostS = host ? getComputedStyle(host) : null;
  return {
    classList: n.className,
    ariaPressed: n.getAttribute('aria-pressed'),
    color: s.color, background: s.backgroundColor, borderColor: s.borderColor, boxShadow: s.boxShadow,
    accent_on_el: s.getPropertyValue('--studio-accent').trim(),
    accent_on_root: root.getPropertyValue('--studio-accent').trim(),
    accent_on_host: hostS ? hostS.getPropertyValue('--studio-accent').trim() : 'no-host',
    bodyTheme: document.body.className + ' | html=' + document.documentElement.className,
  };
});
console.log(JSON.stringify(info, null, 2));
await browser.close();
