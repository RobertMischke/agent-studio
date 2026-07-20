import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * Embed navigation contract: a page inside the preview iframe may report its
 * SPA navigations via
 * `window.parent.postMessage({ source: 'url-preview-embed', type: 'navigation', url: location.href }, '*')`
 * and the preview address bar mirrors the reported URL (display only, no
 * remount). Returns the reported URL when the message comes from the given
 * iframe and honestly names its own origin; null otherwise.
 */
export function parseEmbedNavigation(event: MessageEvent, frame: HTMLIFrameElement | null): string | null {
  if (!frame || event.source !== frame.contentWindow) return null;
  const data = event.data as { source?: unknown; type?: unknown; url?: unknown } | null;
  if (data?.source !== 'url-preview-embed' || data.type !== 'navigation' || typeof data.url !== 'string') return null;
  try {
    return new URL(data.url).origin === event.origin ? data.url : null;
  } catch {
    return null;
  }
}

/**
 * Component-scoped session navigation state for the URL preview address bar:
 * the typed override target plus the live URL the embedded page reported via
 * the url-preview-embed contract above. Neither ever touches the registry.
 */
@Injectable()
export class ProjectUrlEmbedNavigationController {
  private frame: HTMLIFrameElement | null = null;

  /** Session-only navigation target typed into the address bar. */
  readonly overrideUrl = signal<string | null>(null);
  /** Live URL the embedded page reported; display only, never remounts. */
  readonly reportedUrl = signal<string | null>(null);

  constructor() {
    window.addEventListener('message', this.onMessage);
    inject(DestroyRef).onDestroy(() => window.removeEventListener('message', this.onMessage));
  }

  /** The preview iframe whose messages are trusted (null while unmounted). */
  attachFrame(frame: HTMLIFrameElement | null): void {
    this.frame = frame;
  }

  /** Enter in the address bar; the unchanged record URL clears the override. */
  navigate(target: string, recordUrl: string): void {
    this.overrideUrl.set(target === recordUrl ? null : target);
  }

  clearReported(): void {
    this.reportedUrl.set(null);
  }

  reset(): void {
    this.overrideUrl.set(null);
    this.reportedUrl.set(null);
  }

  private readonly onMessage = (event: MessageEvent): void => {
    const url = parseEmbedNavigation(event, this.frame);
    if (url !== null) this.reportedUrl.set(url);
  };
}
