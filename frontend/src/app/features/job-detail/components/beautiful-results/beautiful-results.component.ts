import {
  ChangeDetectionStrategy,
  Component,
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
import { MarkdownImageLightboxDirective } from '../../../../directives/markdown-image-lightbox.directive';

export type BeautifulResultsViewMode = 'rendered' | 'raw';

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
 * lazy syntax highlighting (stage 2 of the renderer pipeline), and the
 * code-copy buttons emitted by the renderer. Image-click-to-enlarge is
 * delegated to the shared `appMarkdownLightbox` directive on the body
 * container, which forwards to `MediaLightboxService`.
 */
@Component({
  selector: 'app-beautiful-results',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownImageLightboxDirective],
  templateUrl: './beautiful-results.component.html',
  styleUrls: ['./beautiful-results.component.scss']
})
export class BeautifulResultsComponent {
  readonly markdown = input<string>('');
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly viewMode = signal<BeautifulResultsViewMode>('rendered');
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
   * The renderer emits `<button data-results-copy>` headers above `<pre>`
   * code blocks. We use event delegation on the body container so we
   * don't have to thread Angular bindings into the sanitized HTML.
   *
   * The image lightbox path is owned by `appMarkdownLightbox` on the body
   * container - it recognises the `data-results-lightbox` markers the
   * renderer still emits and forwards them to the shared
   * `MediaLightboxService`.
   */
  onBodyClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (!target) return;
    const copyBtn = target.closest<HTMLElement>('[data-results-copy]');
    if (copyBtn) {
      event.preventDefault();
      this.copyCodeFor(copyBtn);
    }
  }

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
