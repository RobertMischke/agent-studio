import { Injectable, signal } from '@angular/core';

/**
 * App-wide image lightbox / enlarge view.
 *
 * Markdown surfaces (task description, activity-log, chat, info-button,
 * results) all render images inline through their own renderer; clicking
 * an image should open a single shared overlay that respects the modal
 * stack (Escape, focus trap, etc.) instead of every surface re-inventing
 * its own zoom widget.
 *
 * Usage:
 *   inject(MediaLightboxService).open({ src, alt });
 *
 * The lightbox component (`<app-media-lightbox>`) is mounted once at the
 * app shell. It binds `active()` to its template and calls `close()` on
 * dismiss. The service owns the modal-stack registration so any caller
 * gets correct Escape ordering for free.
 */
export interface MediaLightboxRequest {
  readonly src: string;
  readonly alt?: string | null;
}

export interface MediaLightboxState {
  readonly src: string;
  readonly alt: string;
}

@Injectable({ providedIn: 'root' })
export class MediaLightboxService {
  readonly active = signal<MediaLightboxState | null>(null);

  open(req: MediaLightboxRequest): void {
    const src = (req.src ?? '').trim();
    if (!src) return;
    this.active.set({ src, alt: (req.alt ?? '').toString() });
  }

  close(): void {
    this.active.set(null);
  }
}
