export const ISOLATED_HTML_CSP =
  "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
  "img-src data:; font-src data:; connect-src 'none'; media-src data:; " +
  "object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; " +
  "form-action 'none'; base-uri 'none'";

export const ISOLATED_HTML_LINK_MESSAGE = 'agent-studio:isolated-html-link';
export const WORKBENCH_DECISION_READY_MESSAGE = 'agent-studio:workbench-decision-ready';
export const WORKBENCH_DECISION_CHANGE_MESSAGE = 'agent-studio:workbench-decision-change';
export const WORKBENCH_DECISION_HYDRATE_MESSAGE = 'agent-studio:workbench-decision-hydrate';

export type IsolatedHtmlNavigation =
  | { kind: 'wiki'; relPath: string }
  | { kind: 'external'; url: string };

/**
 * Wrap repository-authored HTML behind a policy-first document. DOMParser is
 * inert, so artifact scripts do not execute until the fixed wrapper reaches an
 * iframe with `sandbox="allow-scripts"`. The missing `allow-same-origin`
 * capability keeps the resulting document on an opaque origin.
 */
export function buildIsolatedHtmlSrcdoc(
  html: string,
  options: { workbenchDecisions?: boolean } = {},
): string {
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

  if (options.workbenchDecisions) {
    const style = wrapper.createElement('style');
    style.textContent = `
      [data-studio-decision-enhanced] [data-option-id] {
        display: flex !important;
        align-items: flex-start !important;
        gap: .55em !important;
      }
      [data-studio-decision-control] {
        width: 1em !important;
        height: 1em !important;
        min-width: 1em !important;
        margin: .2em 0 0 !important;
        accent-color: AccentColor !important;
      }
      [data-studio-decision-comment] {
        display: block !important;
        width: 100% !important;
        min-height: 3.4em !important;
        box-sizing: border-box !important;
        margin-top: .65em !important;
        border: 1px solid color-mix(in srgb, currentColor 28%, transparent) !important;
        border-radius: .45em !important;
        background: Canvas !important;
        padding: .55em .65em !important;
        color: CanvasText !important;
        font: inherit !important;
        resize: vertical !important;
      }
      [data-studio-decision-control]:focus-visible,
      [data-studio-decision-comment]:focus-visible {
        outline: 2px solid AccentColor !important;
        outline-offset: 2px !important;
      }
      @media (prefers-reduced-motion: reduce) {
        [data-studio-decision-enhanced] * { scroll-behavior: auto !important; }
      }`;
    wrapper.head.append(style);
    const decisions = wrapper.createElement('script');
    decisions.textContent = workbenchDecisionBridgeScript();
    wrapper.body.append(decisions);
  }

  return `<!doctype html>${wrapper.documentElement.outerHTML}`;
}

function workbenchDecisionBridgeScript(): string {
  return `(function () {
    var safeId = /^[A-Za-z0-9_-]{1,80}$/;
    var kinds = { single: true, multi: true, confirm: true };
    var points = [];
    var seen = Object.create(null);
    Array.prototype.forEach.call(document.querySelectorAll('[data-decision-id][data-decision-kind]'), function (point) {
      var id = (point.getAttribute('data-decision-id') || '').trim();
      var kind = (point.getAttribute('data-decision-kind') || '').trim();
      if (!safeId.test(id) || !kinds[kind] || seen[id]) return;
      var optionIds = Object.create(null);
      var options = [];
      Array.prototype.forEach.call(point.querySelectorAll('[data-option-id]'), function (option) {
        var optionId = (option.getAttribute('data-option-id') || '').trim();
        if (!safeId.test(optionId) || optionIds[optionId]) return;
        optionIds[optionId] = true;
        var input = option.querySelector('input[type="checkbox"],input[type="radio"]');
        if (!input) {
          input = document.createElement('input');
          option.insertBefore(input, option.firstChild);
        }
        input.type = kind === 'single' ? 'radio' : 'checkbox';
        if (kind === 'single') input.name = 'studio-decision-' + id;
        input.value = optionId;
        input.setAttribute('data-studio-decision-control', '');
        input.setAttribute('aria-label', (option.getAttribute('data-option-label') || option.textContent || optionId).trim());
        options.push({ id: optionId, input: input });
      });
      if (!options.length) return;
      var marker = point.querySelector('[data-comment]');
      var comment = null;
      if (marker) {
        if (marker.matches('textarea,input[type="text"]')) comment = marker;
        else comment = marker.querySelector('textarea,input[type="text"]');
        if (!comment) {
          comment = document.createElement('textarea');
          marker.appendChild(comment);
        }
        var commentLabel = (marker.getAttribute('data-comment') || marker.getAttribute('aria-label')
          || marker.getAttribute('placeholder') || marker.textContent || 'Optional comment').trim();
        comment.setAttribute('aria-label', commentLabel);
        if (!comment.getAttribute('placeholder')) comment.setAttribute('placeholder', commentLabel);
        comment.setAttribute('data-studio-decision-comment', '');
      }
      point.setAttribute('data-studio-decision-enhanced', '');
      seen[id] = true;
      points.push({ id: id, kind: kind, root: point, options: options, comment: comment });
    });

    function read() {
      return points.map(function (point) {
        return {
          decisionId: point.id,
          kind: point.kind,
          selectedOptionIds: point.options.filter(function (option) { return option.input.checked; })
            .map(function (option) { return option.id; }),
          comment: point.comment && point.comment.value.trim() ? point.comment.value.trim().slice(0, 20000) : null
        };
      });
    }
    function publish(type) { parent.postMessage({ type: type, responses: read() }, '*'); }
    document.addEventListener('change', function (event) {
      if (event.target && event.target.hasAttribute && event.target.hasAttribute('data-studio-decision-control'))
        publish('${WORKBENCH_DECISION_CHANGE_MESSAGE}');
    }, true);
    document.addEventListener('input', function (event) {
      if (event.target && event.target.hasAttribute && event.target.hasAttribute('data-studio-decision-comment'))
        publish('${WORKBENCH_DECISION_CHANGE_MESSAGE}');
    }, true);
    window.addEventListener('message', function (event) {
      var message = event.data;
      if (!message || message.type !== '${WORKBENCH_DECISION_HYDRATE_MESSAGE}' || !Array.isArray(message.responses)) return;
      message.responses.forEach(function (response) {
        var point = points.find(function (candidate) { return candidate.id === response.decisionId; });
        if (!point || response.kind !== point.kind || !Array.isArray(response.selectedOptionIds)) return;
        point.options.forEach(function (option) {
          option.input.checked = response.selectedOptionIds.indexOf(option.id) >= 0;
          option.input.disabled = !!message.readonly;
        });
        if (point.comment) {
          point.comment.value = typeof response.comment === 'string' ? response.comment.slice(0, 20000) : '';
          point.comment.disabled = !!message.readonly;
        }
      });
    });
    publish('${WORKBENCH_DECISION_READY_MESSAGE}');
  })();`;
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
