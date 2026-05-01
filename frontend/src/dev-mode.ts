// Per-checkout DEV-mode visual marker. Runs once before Angular bootstrap so
// the manifest/favicon link tags are swapped before the browser caches them.
//
// Activated by GET /api/environment returning { isDev: true }, which the
// backend derives from appsettings.Local.json (gitignored, only present in
// the dev checkout).

export async function applyDevModeIfFlagged(): Promise<void> {
  let isDev = false;
  try {
    const res = await fetch('/api/environment', { cache: 'no-store' });
    if (res.ok) {
      const data = (await res.json()) as { isDev?: boolean };
      isDev = data.isDev === true;
    }
  } catch {
    // Backend not reachable — fall back to non-dev.
  }
  if (!isDev) return;

  swapLinkHref('link[rel="manifest"]', 'manifest-dev.webmanifest');
  swapLinkHref('link[rel="icon"][type="image/svg+xml"]', 'icons-dev/icon.svg');
  setMetaThemeColor('#f59e0b');
  document.title = 'Agent Task Processor (DEV)';
  injectDevBanner();
}

function swapLinkHref(selector: string, href: string): void {
  const el = document.head.querySelector<HTMLLinkElement>(selector);
  if (el) el.href = href;
}

function setMetaThemeColor(color: string): void {
  const el = document.head.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (el) el.content = color;
}

function injectDevBanner(): void {
  // Top-fixed orange stripe + "DEV" pill — unmistakable but out of the way of
  // the existing header. Body padding shifts the app down so nothing is hidden.
  const style = document.createElement('style');
  style.textContent = `
    body { padding-top: 22px; }
    .dev-banner {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      height: 22px;
      background: repeating-linear-gradient(
        45deg,
        #f59e0b 0 12px,
        #b45309 12px 24px
      );
      color: #1a1208;
      font: 700 11px/22px 'Segoe UI', system-ui, sans-serif;
      letter-spacing: 0.18em;
      text-align: center;
      z-index: 9999;
      pointer-events: none;
      box-shadow: 0 1px 6px rgba(0,0,0,0.4);
      text-transform: uppercase;
    }
  `;
  document.head.appendChild(style);

  const banner = document.createElement('div');
  banner.className = 'dev-banner';
  banner.setAttribute('data-testid', 'dev-banner');
  banner.textContent = 'DEV — local development checkout';
  document.body.appendChild(banner);
}
