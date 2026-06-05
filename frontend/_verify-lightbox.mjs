import { chromium } from '@playwright/test';

const BASE = process.env.PW_BASE_URL || 'http://localhost:4321';
const OUT =
  'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/feature-evidence-gallery-stable-frame-letterbox-small-images/results';

// Deterministic SVG images: naturalWidth/Height == the width/height attrs.
const TINY = `data:image/svg+xml;utf8,${encodeURIComponent(
  `<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><rect width='40' height='40' fill='#f38ba8'/></svg>`
)}`;
const WIDE = `data:image/svg+xml;utf8,${encodeURIComponent(
  `<svg xmlns='http://www.w3.org/2000/svg' width='1600' height='400'><rect width='1600' height='400' fill='#89b4fa'/></svg>`
)}`;
const TALL = `data:image/svg+xml;utf8,${encodeURIComponent(
  `<svg xmlns='http://www.w3.org/2000/svg' width='300' height='1400'><rect width='300' height='1400' fill='#a6e3a1'/></svg>`
)}`;

const results = { checks: [], pass: true };
function check(name, cond, detail) {
  results.checks.push({ name, pass: !!cond, detail });
  if (!cond) results.pass = false;
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}  ${detail ?? ''}`);
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
page.on('console', (m) => { if (m.type() === 'error') console.log('[page error]', m.text()); });

await page.goto(BASE, { waitUntil: 'domcontentloaded' });

// Wait until the app shell has mounted the lightbox host AND Angular's dev
// global is available so we can drive the root service directly.
await page.waitForFunction(
  () => !!document.querySelector('app-media-lightbox') && !!window.ng?.getComponent,
  { timeout: 30000 }
);

async function openGallery(images, index) {
  await page.evaluate(
    ({ images, index }) => {
      const el = document.querySelector('app-media-lightbox');
      const cmp = window.ng.getComponent(el);
      cmp.lightbox.openGallery({ images, index });
      window.ng.applyChanges?.(cmp);
    },
    { images, index }
  );
  await page.locator('[data-testid="media-lightbox-stage"]').waitFor({ state: 'visible', timeout: 5000 });
  // settle a frame for layout
  await page.waitForTimeout(120);
}

async function metrics() {
  return await page.evaluate(() => {
    const stage = document.querySelector('[data-testid="media-lightbox-stage"]');
    const img = document.querySelector('[data-testid="media-lightbox-image"]');
    const s = stage.getBoundingClientRect();
    const i = img.getBoundingClientRect();
    return {
      stage: { w: Math.round(s.width), h: Math.round(s.height) },
      img: { w: Math.round(i.width), h: Math.round(i.height) },
      natural: { w: img.naturalWidth, h: img.naturalHeight },
      fit: getComputedStyle(img).objectFit,
    };
  });
}

// --- Open a 3-image gallery: tiny, wide, tall ------------------------------
await openGallery([{ src: TINY }, { src: WIDE }, { src: TALL }], 0);
const m0 = await metrics();
await page.screenshot({ path: `${OUT}/verify-1-small-letterbox.png` });

// Requirement 2: small image NOT upscaled -> rendered size == natural size,
// and strictly smaller than the stage in both axes (neutral bars around it).
check('small image not upscaled (rendered==natural)',
  m0.img.w === m0.natural.w && m0.img.h === m0.natural.h,
  `rendered ${m0.img.w}x${m0.img.h} natural ${m0.natural.w}x${m0.natural.h}`);
check('small image leaves letterbox+pillarbox bars (img < stage both axes)',
  m0.img.w < m0.stage.w && m0.img.h < m0.stage.h,
  `img ${m0.img.w}x${m0.img.h} stage ${m0.stage.w}x${m0.stage.h}`);
check('img object-fit is contain', m0.fit === 'contain', m0.fit);

// Page to the wide image.
await page.locator('[data-testid="media-lightbox-next"]').click();
await page.waitForTimeout(120);
const m1 = await metrics();
await page.screenshot({ path: `${OUT}/verify-2-wide-letterbox.png` });

// Requirement 1: stage size is identical across images (no layout jump).
check('stage size stable: small vs wide',
  m0.stage.w === m1.stage.w && m0.stage.h === m1.stage.h,
  `small ${m0.stage.w}x${m0.stage.h} wide ${m1.stage.w}x${m1.stage.h}`);
// Wide image fills width, letterboxed in height (img height < stage height).
check('wide image letterboxed (img height < stage height)',
  m1.img.h < m1.stage.h && m1.img.w <= m1.stage.w,
  `img ${m1.img.w}x${m1.img.h} stage ${m1.stage.w}x${m1.stage.h}`);

// Page to the tall image.
await page.locator('[data-testid="media-lightbox-next"]').click();
await page.waitForTimeout(120);
const m2 = await metrics();
await page.screenshot({ path: `${OUT}/verify-3-tall-pillarbox.png` });

check('stage size stable: small vs tall',
  m0.stage.w === m2.stage.w && m0.stage.h === m2.stage.h,
  `small ${m0.stage.w}x${m0.stage.h} tall ${m2.stage.w}x${m2.stage.h}`);
check('tall image pillarboxed (img width < stage width)',
  m2.img.w < m2.stage.w && m2.img.h <= m2.stage.h,
  `img ${m2.img.w}x${m2.img.h} stage ${m2.stage.w}x${m2.stage.h}`);

// Requirement 5: zoom toggle -> intrinsic size.
await page.locator('[data-testid="media-lightbox-prev"]').click(); // back to wide
await page.waitForTimeout(80);
await page.locator('[data-testid="media-lightbox-image"]').click(); // toggle zoom
await page.waitForTimeout(120);
const mz = await metrics();
await page.screenshot({ path: `${OUT}/verify-4-zoom-original.png` });
check('zoom shows intrinsic pixels (rendered width == natural width)',
  mz.img.w === mz.natural.w,
  `rendered ${mz.img.w} natural ${mz.natural.w}`);

// Navigating must reset zoom back to fitted.
await page.locator('[data-testid="media-lightbox-next"]').click();
await page.waitForTimeout(120);
const after = await metrics();
check('zoom resets on navigation (img refits to stage)',
  after.img.w <= after.stage.w && after.img.h <= after.stage.h,
  `img ${after.img.w}x${after.img.h} stage ${after.stage.w}x${after.stage.h}`);

results.metrics = { small: m0, wide: m1, tall: m2, zoomed: mz };

const fs = await import('node:fs');
fs.writeFileSync(`${OUT}/verify-report.json`, JSON.stringify(results, null, 2));
console.log('\nVERIFY_RESULT=' + (results.pass ? 'PASS' : 'FAIL'));

await browser.close();
process.exit(results.pass ? 0 : 1);
