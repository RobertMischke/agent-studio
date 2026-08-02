export const ISOLATED_HTML_CSP =
  "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
  "img-src data:; font-src data:; connect-src 'none'; media-src data:; " +
  "object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; " +
  "form-action 'none'; base-uri 'none'";

export const ISOLATED_HTML_LINK_MESSAGE = 'agent-studio:isolated-html-link';

export type IsolatedHtmlNavigation =
  | { kind: 'wiki'; relPath: string }
  | { kind: 'external'; url: string };

/**
 * Wrap repository-authored HTML behind a policy-first document. DOMParser is
 * inert, so artifact scripts do not execute until the fixed wrapper reaches an
 * iframe with `sandbox="allow-scripts"`. The missing `allow-same-origin`
 * capability keeps the resulting document on an opaque origin.
 */
export function buildIsolatedHtmlSrcdoc(html: string): string {
  if (!html) return '';
  const parser = new DOMParser();
  const artifact = parser.parseFromString(html, 'text/html');
  const wrapper = parser.parseFromString(
    '<!doctype html><html><head></head><body></body></html>',
    'text/html',
  );

  for (const control of Array.from(artifact.querySelectorAll('base, meta'))) {
    if (isArtifactSecurityControl(control)) control.remove();
  }

  const policy = wrapper.createElement('meta');
  policy.httpEquiv = 'Content-Security-Policy';
  policy.content = ISOLATED_HTML_CSP;
  const base = wrapper.createElement('base');
  base.href = 'about:blank';
  wrapper.head.append(policy, base);

  copyAttributes(artifact.documentElement, wrapper.documentElement);
  copyAttributes(artifact.head, wrapper.head);
  copyAttributes(artifact.body, wrapper.body);
  for (const node of Array.from(artifact.head.childNodes))
    wrapper.head.append(wrapper.importNode(node, true));
  for (const node of Array.from(artifact.body.childNodes))
    wrapper.body.append(wrapper.importNode(node, true));

  // `base=about:blank` also disables in-page anchors. Restore scrolling inside
  // the frame and delegate every other link to the trusted host. The host
  // verifies the sending iframe and resolves the raw href against the current
  // repository path before it performs any navigation.
  const nav = wrapper.createElement('script');
  nav.textContent = `document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    var href = a.getAttribute('href') || '';
    e.preventDefault();
    if (href.charAt(0) === '#') {
      var el = document.getElementById(href.slice(1))
        || document.querySelector('a[name="' + href.slice(1).replace(/"/g, '') + '"]');
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return;
    }
    parent.postMessage({ type: '${ISOLATED_HTML_LINK_MESSAGE}', href: href }, '*');
  }, true);`;
  wrapper.body.append(nav);

  return `<!doctype html>${wrapper.documentElement.outerHTML}`;
}

/**
 * Resolve a link reported by an isolated HTML frame without granting that
 * frame browser-navigation capability. Only repository-relative paths that
 * remain under `docs/` may enter the Wiki; absolute HTTP(S) links are external.
 */
export function resolveIsolatedHtmlNavigation(
  entryPath: string,
  href: string,
): IsolatedHtmlNavigation | null {
  const target = href.trim();
  if (!target || target.startsWith('#')) return null;

  if (/^https?:\/\//i.test(target) || target.startsWith('//')) {
    try {
      const url = new URL(target, 'https://external.invalid/');
      return url.protocol === 'http:' || url.protocol === 'https:'
        ? { kind: 'external', url: url.href }
        : null;
    } catch {
      return null;
    }
  }
  if (/^[a-z][a-z0-9+.-]*:/i.test(target)) return null;

  const cleanEntryPath = entryPath.trim().replaceAll('\\', '/').replace(/^\/+/, '');
  if (!cleanEntryPath.startsWith('docs/')) return null;
  try {
    const resolved = new URL(target.replaceAll('\\', '/'), `https://repository.invalid/${cleanEntryPath}`);
    if (resolved.origin !== 'https://repository.invalid') return null;
    const decodedPath = decodeURIComponent(resolved.pathname).replace(/^\/+/, '');
    if (!decodedPath.startsWith('docs/')) return null;
    const relPath = decodedPath.slice('docs/'.length);
    return relPath ? { kind: 'wiki', relPath } : null;
  } catch {
    return null;
  }
}

function copyAttributes(source: Element, target: Element): void {
  for (const attribute of Array.from(source.attributes))
    target.setAttribute(attribute.name, attribute.value);
}

function isArtifactSecurityControl(node: Node): boolean {
  if (!(node instanceof HTMLBaseElement || node instanceof HTMLMetaElement)) return false;
  if (node instanceof HTMLBaseElement) return true;
  const httpEquiv = node.httpEquiv.trim().toLowerCase();
  return httpEquiv === 'content-security-policy'
    || httpEquiv === 'content-security-policy-report-only'
    || httpEquiv === 'refresh';
}
