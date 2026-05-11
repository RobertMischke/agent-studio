// Per-checkout DEV-mode visual marker. Runs once before Angular bootstrap so
// the manifest/favicon link tags are swapped before the browser caches them.
//
// Activated by GET /api/environment returning { isDev: true }, which the
// backend derives from appsettings.Local.json (gitignored, only present in
// the dev checkout).
//
// Why the favicon is an inline data URL: Chrome treats `<link rel="icon">`
// differently from normal fetches — once it picks up the original href from
// HTML, mutating .href (or even removing+reinserting the element) does not
// reliably trigger a re-fetch within the same tab. Embedding the dev icon
// as a data URL bypasses fetch and cache entirely, so the orange icon shows
// up immediately on every load.

const DEV_ICON_SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0%" stop-color="#3a2a0f"/>
      <stop offset="100%" stop-color="#1a1208"/>
    </linearGradient>
    <linearGradient id="board" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#fbbf24"/>
      <stop offset="100%" stop-color="#f59e0b"/>
    </linearGradient>
  </defs>
  <rect width="512" height="512" rx="108" fill="url(#bg)"/>
  <rect x="72" y="100" width="108" height="312" rx="20" fill="url(#board)" opacity="0.3"/>
  <rect x="202" y="100" width="108" height="312" rx="20" fill="url(#board)" opacity="0.3"/>
  <rect x="332" y="100" width="108" height="312" rx="20" fill="url(#board)" opacity="0.3"/>
  <rect x="88" y="128" width="76" height="52" rx="12" fill="#fbbf24"/>
  <rect x="88" y="196" width="76" height="52" rx="12" fill="#fbbf24" opacity="0.6"/>
  <rect x="218" y="128" width="76" height="52" rx="12" fill="#fb923c"/>
  <rect x="218" y="196" width="76" height="72" rx="12" fill="#fb923c" opacity="0.6"/>
  <rect x="348" y="128" width="76" height="52" rx="12" fill="#f97316"/>
  <polyline points="366,150 380,164 400,140" fill="none" stroke="#fff" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>
  <g transform="translate(512,512)">
    <polygon points="0,0 -200,0 0,-200" fill="#dc2626"/>
    <text x="-58" y="-58" fill="#ffffff" font-family="Segoe UI, system-ui, sans-serif" font-weight="900" font-size="64" text-anchor="middle" transform="rotate(-45 -58 -58)" letter-spacing="4">DEV</text>
  </g>
</svg>`;

const DEV_ICON_DATA_URL = `data:image/svg+xml;utf8,${encodeURIComponent(DEV_ICON_SVG)}`;

export async function applyDevModeIfFlagged(): Promise<void> {
  let isDev = false;
  try {
    // Pass X-Client-Id even though /api/environment is read-only — the
    // CodePatternDriftAnalysisService rule frontend-fetch-xclientid enforces
    // this on every raw fetch() to /api so production-shape matches drill paths.
    const res = await fetch('/api/environment', {
      cache: 'no-store',
      headers: { 'X-Client-Id': 'local-default' },
    });
    if (res.ok) {
      const data = (await res.json()) as { isDev?: boolean };
      isDev = data.isDev === true;
    }
  } catch {
    // Backend not reachable — fall back to non-dev.
  }
  if (!isDev) return;

  // Mutating link.href on an existing <link rel="icon"> does NOT make Chrome
  // re-fetch — once the browser has parsed the original tag and started its
  // favicon fetch, it ignores subsequent href changes. We have to remove the
  // element and insert a fresh one so the browser treats it as a new request.
  replaceLink('link[rel="manifest"]', { rel: 'manifest', href: 'manifest-dev.webmanifest' });
  replaceLink('link[rel="icon"][type="image/svg+xml"]', { rel: 'icon', type: 'image/svg+xml', href: DEV_ICON_DATA_URL });
  // The legacy .ico fallback would still point at the non-dev favicon; in
  // dev mode just drop it so the SVG above is the sole favicon source.
  document.head.querySelector('link[rel="icon"][type="image/x-icon"]')?.remove();
  setMetaThemeColor('#f59e0b');
  document.title = 'Agent Software Studio (DEV)';
  injectDevBanner();
}

function replaceLink(selector: string, attrs: Record<string, string>): void {
  const old = document.head.querySelector<HTMLLinkElement>(selector);
  if (old) old.remove();
  const link = document.createElement('link');
  for (const [k, v] of Object.entries(attrs)) link.setAttribute(k, v);
  document.head.appendChild(link);
}

function setMetaThemeColor(color: string): void {
  const el = document.head.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (el) el.content = color;
}

function injectDevBanner(): void {
  // Keep the dev checkout unmistakable without stealing vertical workspace.
  // The marker is intentionally fixed to the left edge and does not mutate
  // body padding, so screenshots and dense workbench views keep their height.
  const style = document.createElement('style');
  style.textContent = `
    body::before {
      content: "";
      position: fixed;
      top: 0;
      bottom: 0;
      left: 0;
      width: 3px;
      background: linear-gradient(180deg, #f59e0b, #dc2626);
      z-index: 9998;
      pointer-events: none;
    }
    .dev-banner {
      position: fixed;
      left: 0;
      top: 96px;
      width: 18px;
      height: 70px;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: 0 6px 6px 0;
      background: rgba(245, 158, 11, 0.82);
      color: #1a1208;
      font: 800 10px/1 'Segoe UI', system-ui, sans-serif;
      letter-spacing: 0.14em;
      z-index: 9999;
      pointer-events: none;
      box-shadow: 0 2px 10px rgba(0,0,0,0.22);
      text-transform: uppercase;
      writing-mode: vertical-rl;
      transform: rotate(180deg);
    }
  `;
  document.head.appendChild(style);

  const banner = document.createElement('div');
  banner.className = 'dev-banner';
  banner.setAttribute('data-testid', 'dev-banner');
  banner.setAttribute('aria-label', 'DEV local development checkout');
  banner.textContent = 'DEV';
  document.body.appendChild(banner);
}
