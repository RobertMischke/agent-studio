export const ISOLATED_HTML_CSP =
  "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
  "img-src data:; font-src data:; connect-src 'none'; media-src data:; " +
  "object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; " +
  "form-action 'none'; base-uri 'none'";

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
  // the frame while swallowing every external navigation.
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
    }
  }, true);`;
  wrapper.body.append(nav);

  return `<!doctype html>${wrapper.documentElement.outerHTML}`;
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

