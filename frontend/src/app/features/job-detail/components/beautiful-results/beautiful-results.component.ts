import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal
} from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { renderResultsHtml, type SentinelBanner } from './beautiful-results.renderer';
import { applyHighlighting } from './beautiful-results.highlight';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ModalStackService } from '../../../../services/modal-stack.service';

export type BeautifulResultsViewMode = 'rendered' | 'raw';

interface Lightbox {
  src: string;
  alt: string;
}

interface SentinelMeta {
  kind: SentinelBanner['kind'];
  label: string;
  icon: string;
}

const SENTINEL_META: Record<SentinelBanner['kind'], SentinelMeta> = {
  done:       { kind: 'done',       label: 'Task complete',   icon: '✓' },
  blocked:    { kind: 'blocked',    label: 'Task blocked',    icon: '⚠' },
  needsInput: { kind: 'needsInput', label: 'Needs input',     icon: '?' },
  noop:       { kind: 'noop',       label: 'No action taken', icon: '○' }
};

/**
 * Beautiful, read-only renderer for a finished job's result markdown.
 *
 * Inputs:
 *   markdown      — the source `status.md` content
 *   jobId / watchPath — used by the image resolver to turn `attachments/foo.png`
 *                       and `results/foo.png` into job-folder API URLs
 *
 * The component owns: sentinel banner extraction, the Rendered/Raw toggle,
 * lazy syntax highlighting (stage 2 of the renderer pipeline), image
 * lightbox, and the code-copy buttons emitted by the renderer.
 */
@Component({
  selector: 'app-beautiful-results',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './beautiful-results.component.html',
  styleUrls: ['./beautiful-results.component.scss']
})
export class BeautifulResultsComponent {
  readonly markdown = input<string>('');
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly viewMode = signal<BeautifulResultsViewMode>('rendered');
  readonly lightbox = signal<Lightbox | null>(null);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly sanitizer = inject(DomSanitizer);
  @ViewChild('body') private bodyRef?: ElementRef<HTMLElement>;

  private readonly rendered = computed(() => renderResultsHtml(this.markdown(), {
    jobId: this.jobId(),
    watchPath: this.watchPath()
  }));

  readonly banner = computed<SentinelMeta & { reason: string | null } | null>(() => {
    const b = this.rendered().banner;
    if (!b) return null;
    return { ...SENTINEL_META[b.kind], reason: b.reason };
  });

  readonly bodyHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(this.rendered().html)
  );

  readonly hasBody = computed(() => this.rendered().html.trim().length > 0);

  /**
   * Whenever the rendered HTML changes (new job, new markdown, raw->rendered
   * toggle), kick off stage 2: lazy highlight.js for code blocks. The await
   * happens off-cycle so the synchronous "first paint" is unblocked.
   */
  private readonly highlightEffect = effect(() => {
    // Read deps so this effect re-runs on real changes.
    this.bodyHtml();
    if (this.viewMode() !== 'rendered') return;
    queueMicrotask(() => {
      void applyHighlighting(this.bodyRef?.nativeElement ?? null);
    });
  });

  setMode(mode: BeautifulResultsViewMode): void {
    this.viewMode.set(mode);
  }

  /**
   * The renderer emits decorated `<button data-results-lightbox>` wrappers
   * around `<figure>` images and `<button data-results-copy>` headers above
   * `<pre>` code blocks. We use event delegation on the body container so we
   * don't have to thread Angular bindings into the sanitized HTML.
   */
  onBodyClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (!target) return;
    const lightboxBtn = target.closest<HTMLElement>('[data-results-lightbox]');
    if (lightboxBtn) {
      event.preventDefault();
      this.lightbox.set({
        src: lightboxBtn.getAttribute('data-results-lightbox') || '',
        alt: lightboxBtn.getAttribute('data-results-alt') || ''
      });
      return;
    }
    const copyBtn = target.closest<HTMLElement>('[data-results-copy]');
    if (copyBtn) {
      event.preventDefault();
      this.copyCodeFor(copyBtn);
    }
  }

  closeLightbox(): void {
    this.lightbox.set(null);
  }

  // Lightbox Escape routes through ModalStack (effect below) so a
  // confirm-dialog above it always wins. The previous local handler is
  // gone; template references to `onLightboxKey` were dropped along with it.
  private readonly lightboxModalStack = inject(ModalStackService);
  private readonly lightboxDestroyRef = inject(DestroyRef);
  private lightboxStackDispose: (() => void) | null = null;
  private readonly lightboxStackEffect = effect(() => {
    const open = this.lightbox() !== null;
    if (open) {
      if (!this.lightboxStackDispose) {
        this.lightboxStackDispose = this.lightboxModalStack.push('beautiful-results-lightbox', () => this.closeLightbox());
      }
    } else if (this.lightboxStackDispose) {
      this.lightboxStackDispose();
      this.lightboxStackDispose = null;
    }
  });

  private async copyCodeFor(button: HTMLElement): Promise<void> {
    const pre = button.closest('.results-code')?.querySelector<HTMLElement>('[data-results-code]');
    if (!pre) return;
    // Prefer the rendered text so already-highlighted blocks still copy clean.
    const text = pre.textContent ?? '';
    const ok = await copyTextToClipboard(text);
    const next: 'copied' | 'failed' = ok ? 'copied' : 'failed';
    this.copyState.set(next);
    button.textContent = next === 'copied' ? '✓ Copied' : '⚠ Failed';
    if (this.copyTimer != null) clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => {
      button.textContent = 'Copy';
      this.copyState.set('idle');
      this.copyTimer = null;
    }, 1600);
  }
}
