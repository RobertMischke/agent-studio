import {
  AfterViewInit,
  Directive,
  ElementRef,
  HostListener,
  OnDestroy,
  inject,
} from '@angular/core';
import { MediaLightboxService } from '../services/media-lightbox.service';

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
 * `MediaLightboxService` with the image's `src` / `alt`.
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
      this.lightbox.open({
        src: legacy.getAttribute('data-results-lightbox') ?? '',
        alt: legacy.getAttribute('data-results-alt') ?? '',
      });
      return;
    }
    const img = target.closest<HTMLImageElement>('img');
    if (!img || !this.host.nativeElement.contains(img)) return;
    if (!isUsableSrc(img.getAttribute('src'))) return;
    event.preventDefault();
    this.lightbox.open({
      src: img.currentSrc || img.src,
      alt: img.getAttribute('alt') ?? '',
    });
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
    this.lightbox.open({
      src: img.currentSrc || img.src,
      alt: img.getAttribute('alt') ?? '',
    });
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
