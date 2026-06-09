import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { renderResultsHtml, type SentinelBanner } from './beautiful-results.renderer';
import { applyHighlighting } from './beautiful-results.highlight';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { MarkdownImageLightboxDirective } from '../../../../directives/markdown-image-lightbox.directive';
import { perfMark, perfMeasure } from '../../../../utils/perf-tracker';

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
 * Read-only renderer for a finished job's result markdown.
 *
 * The rendered/raw toggle lives in the protocol-pane context menu (F54).
 * This component always renders in "rendered" mode; the parent switches
 * to a raw `<pre>` when the user picks "View raw markdown".
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

  /** Emitted when the operator clicks a detected source reference. */
  readonly openSource = output<{ path: string; line: number | null }>();

  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly sanitizer = inject(DomSanitizer);
  @ViewChild('body') private bodyRef?: ElementRef<HTMLElement>;

  private readonly rendered = computed(() => {
    perfMark('markdown-render');
    const out = renderResultsHtml(this.markdown(), {
      jobId: this.jobId(),
      watchPath: this.watchPath()
    });
    perfMark('markdown-rendered');
    perfMeasure('beautiful-results-render', 'markdown-render', 'markdown-rendered');
    return out;
  });

  readonly banner = computed<SentinelMeta & { reason: string | null } | null>(() => {
    const b = this.rendered().banner;
    if (!b) return null;
    return { ...SENTINEL_META[b.kind], reason: b.reason };
  });

  readonly bodyHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(this.rendered().html)
  );

  readonly hasBody = computed(() => this.rendered().html.trim().length > 0);

  private readonly highlightEffect = effect(() => {
    this.bodyHtml();
    queueMicrotask(() => {
      void applyHighlighting(this.bodyRef?.nativeElement ?? null);
    });
  });

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
  onBodyClick(event: Event): void {
    const target = event.target as HTMLElement | null;
    if (!target) return;
    const copyBtn = target.closest<HTMLElement>('[data-results-copy]');
    if (copyBtn) {
      event.preventDefault();
      this.copyCodeFor(copyBtn);
      return;
    }
    const sourceBtn = target.closest<HTMLElement>('[data-results-source]');
    if (sourceBtn) {
      event.preventDefault();
      const path = sourceBtn.getAttribute('data-results-source');
      if (!path) return;
      const rawLine = sourceBtn.getAttribute('data-results-line');
      const line = rawLine ? Number.parseInt(rawLine, 10) : NaN;
      this.openSource.emit({ path, line: Number.isFinite(line) ? line : null });
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
