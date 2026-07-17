export type PreviewFrameState =
  | 'navigating'
  | 'rendered'
  | 'unconfirmed'
  | 'blocked'
  | 'blank'
  | 'error';

export interface PreviewFrameInspection {
  state: Exclude<PreviewFrameState, 'navigating'>;
  detail: string;
}

export function normalizedPreviewUrl(value: string): string {
  try {
    return new URL(value, window.location.href).href;
  } catch {
    return value;
  }
}

export function isSameOriginPreview(value: string): boolean {
  try {
    return new URL(value, window.location.href).origin === window.location.origin;
  } catch {
    return false;
  }
}

/**
 * Inspect a loaded iframe when the browser permits DOM access. A cross-origin
 * document is deliberately reported as unconfirmed: a load event alone cannot
 * distinguish useful content from the browser's own empty/error document.
 */
export function inspectPreviewFrame(
  frame: HTMLIFrameElement,
  configuredUrl: string,
): PreviewFrameInspection {
  const expected = normalizedPreviewUrl(configuredUrl);
  const effective = normalizedPreviewUrl(frame.src || frame.getAttribute('src') || 'about:blank');
  if (effective !== expected) {
    return { state: 'blank', detail: `The frame stayed on ${effective || 'about:blank'}.` };
  }

  if (!isSameOriginPreview(configuredUrl)) {
    return {
      state: 'unconfirmed',
      detail: 'The server responded, but browser origin rules prevent Studio from confirming its rendered content.',
    };
  }

  let doc: Document | null;
  try {
    doc = frame.contentDocument;
  } catch {
    return {
      state: 'blocked',
      detail: 'The browser denied access to a same-origin frame, usually because framing was blocked by CSP or X-Frame-Options.',
    };
  }

  if (!doc) {
    return {
      state: 'blocked',
      detail: 'The browser did not expose the loaded document. CSP or X-Frame-Options may have blocked it.',
    };
  }

  const documentUrl = normalizedPreviewUrl(doc.URL || 'about:blank');
  if (documentUrl === 'about:blank') {
    return { state: 'blank', detail: 'The frame fired load but remained on about:blank.' };
  }
  if (documentUrl.startsWith('chrome-error:') || documentUrl.startsWith('edge-error:')) {
    return { state: 'error', detail: 'The browser loaded an internal navigation error document.' };
  }
  if (documentUrl !== expected) {
    return { state: 'error', detail: `The frame navigated to ${documentUrl} instead of the configured URL.` };
  }

  const body = doc.body;
  if (!body) {
    return { state: 'blank', detail: 'The loaded document has no body.' };
  }
  const text = (body.innerText || body.textContent || '').replace(/\s+/g, ' ').trim();
  const visualContent = body.querySelector(
    'img, svg, canvas, video, audio, object, embed, table, form, input, button, [role]',
  );
  const rootWidth = Math.max(doc.documentElement?.scrollWidth ?? 0, body.scrollWidth ?? 0);
  const rootHeight = Math.max(doc.documentElement?.scrollHeight ?? 0, body.scrollHeight ?? 0);
  if (!text && !visualContent) {
    return {
      state: 'blank',
      detail: `The loaded body is empty (${rootWidth} x ${rootHeight}px).`,
    };
  }

  return {
    state: 'rendered',
    detail: `Rendered body confirmed (${rootWidth} x ${rootHeight}px, ${text.length} text characters).`,
  };
}
