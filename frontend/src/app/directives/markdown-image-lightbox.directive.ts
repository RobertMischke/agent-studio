import {
  AfterViewInit,
  Directive,
  ElementRef,
  HostListener,
  OnDestroy,
  inject,
} from '@angular/core';
import {
  MediaLightboxImage,
  MediaLightboxService,
} from '../services/media-lightbox.service';

/**
 * Click-to-enlarge for markdown-rendered images.
 *
 * Markdown surfaces (task description history, activity-log, chat,
 * info-button, beautiful-results) render their bodies via `[innerHTML]`,
 * so we cannot bind Angular event handlers onto individual `<img>` tags.
 * This directive sits on the container and uses event delegation:
 *
 *   <div appMarkdownLightbox [innerHTML]="bodyHtml"></div>
 *
 * On click within the host, the directive walks up from `event.target`
 * looking for an `<img>` (or a wrapper carrying `data-results-lightbox`
 * from the legacy beautiful-results renderer) and opens
 * `MediaLightboxService`. It hands over *every* usable image in the host
 * as a gallery plus the index of the one that was clicked, so the
 * lightbox can page the surface's images with the arrow keys (evidence /
 * results screenshots, a chat thread with several attachments, ...).
 *
 * Accessibility:
 *  - On view init and on any DOM mutation under the host (new agent text
 *    streaming in, etc.), every direct `<img>` gets `tabindex="0"`,
 *    `role="button"` and an `aria-label` so screen readers / keyboard
 *    users can activate it via Enter/Space.
 *  - The host also listens for Enter/Space and forwards to the same
 *    open-lightbox path so an `<img>` that received focus opens the
 *    preview without a mouse.
 *  - Escape is handled by the lightbox component via `ModalStackService`,
 *    not here.
 *
 * Pure inline `<img>` elements without a meaningful `src` are skipped
 * (broken upload placeholders, etc.).
 */
@Directive({
  selector: '[appMarkdownLightbox]',
  standalone: true,
})
export class MarkdownImageLightboxDirective implements AfterViewInit, OnDestroy {
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly lightbox = inject(MediaLightboxService);

  private observer: MutationObserver | null = null;

  ngAfterViewInit(): void {
    this.markImages();
    if (typeof MutationObserver !== 'undefined') {
      this.observer = new MutationObserver(() => this.markImages());
      this.observer.observe(this.host.nativeElement, {
        childList: true,
        subtree: true,
      });
    }
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
    this.observer = null;
  }

  @HostListener('click', ['$event'])
  onClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (!target) return;
    // Beautiful-results wraps figures in a button with data-results-lightbox.
    // Honour that legacy attribute first so we can migrate without renderer churn.
    const legacy = target.closest<HTMLElement>('[data-results-lightbox]');
    if (legacy) {
      event.preventDefault();
      this.openGalleryAt(legacy);
      return;
    }
    const img = target.closest<HTMLImageElement>('img');
    if (!img || !this.host.nativeElement.contains(img)) return;
    if (!isUsableSrc(img.getAttribute('src'))) return;
    event.preventDefault();
    this.openGalleryAt(img);
  }

  @HostListener('keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' && event.key !== ' ' && event.key !== 'Spacebar') return;
    const target = event.target as HTMLElement | null;
    if (!target) return;
    if (target.tagName !== 'IMG') return;
    const img = target as HTMLImageElement;
    if (!isUsableSrc(img.getAttribute('src'))) return;
    event.preventDefault();
    this.openGalleryAt(img);
  }

  /**
   * Open the lightbox as a gallery of every usable image under the host,
   * positioned on the `anchor` the user activated. Falls back to a
   * single-image open if the anchor cannot be located in the collected
   * set (defensive - should not happen for in-DOM elements).
   */
  private openGalleryAt(anchor: HTMLElement): void {
    const { images, anchors } = this.collectGallery();
    if (images.length === 0) return;
    const index = anchors.indexOf(anchor);
    this.lightbox.openGallery({ images, index: index < 0 ? 0 : index });
  }

  /**
   * Collect the host's images in document order. Each entry pairs the
   * lightbox payload with the DOM element that represents it (the bare
   * `<img>`, or the legacy `[data-results-lightbox]` button wrapper) so a
   * click target can be mapped back to a gallery index. The wrapper's
   * data attributes win over the inner `<img>` so a thumbnail that points
   * at a full-res asset still enlarges the full-res one.
   */
  private collectGallery(): { images: MediaLightboxImage[]; anchors: HTMLElement[] } {
    const images: MediaLightboxImage[] = [];
    const anchors: HTMLElement[] = [];
    const seenWrappers = new Set<HTMLElement>();
    this.host.nativeElement
      .querySelectorAll<HTMLImageElement>('img')
      .forEach((img) => {
        const wrapper = img.closest<HTMLElement>('[data-results-lightbox]');
        if (wrapper) {
          if (seenWrappers.has(wrapper)) return;
          seenWrappers.add(wrapper);
          const src = wrapper.getAttribute('data-results-lightbox') ?? '';
          if (!isUsableSrc(src)) return;
          images.push({ src, alt: wrapper.getAttribute('data-results-alt') ?? '' });
          anchors.push(wrapper);
          return;
        }
        if (!isUsableSrc(img.getAttribute('src'))) return;
        images.push({ src: img.currentSrc || img.src, alt: img.getAttribute('alt') ?? '' });
        anchors.push(img);
      });
    return { images, anchors };
  }

  private markImages(): void {
    const root = this.host.nativeElement;
    const images = root.querySelectorAll<HTMLImageElement>('img');
    images.forEach((img) => {
      if (img.dataset['mdLightboxBound'] === '1') return;
      // Skip images that already live inside a button wrapper (beautiful-
      // results legacy markup): the wrapper carries focus, the bare img
      // would create a duplicate tab stop.
      if (img.closest('[data-results-lightbox]')) {
        img.dataset['mdLightboxBound'] = '1';
        return;
      }
      if (!isUsableSrc(img.getAttribute('src'))) return;
      img.setAttribute('tabindex', '0');
      img.setAttribute('role', 'button');
      if (!img.hasAttribute('aria-label')) {
        const alt = img.getAttribute('alt') ?? '';
        img.setAttribute(
          'aria-label',
          alt ? `Open image: ${alt}` : 'Open image preview',
        );
      }
      img.classList.add('md-image-zoomable');
      img.dataset['mdLightboxBound'] = '1';
    });
  }
}

function isUsableSrc(src: string | null | undefined): boolean {
  if (!src) return false;
  const trimmed = src.trim();
  if (!trimmed) return false;
  // Skip the 1x1 transparent placeholder TipTap uses while uploading.
  if (trimmed.startsWith('data:image/gif;base64,R0lGOD')) return false;
  return true;
}
