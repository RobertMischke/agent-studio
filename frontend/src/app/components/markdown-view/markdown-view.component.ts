import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
} from '@angular/core';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { MarkdownImageLightboxDirective } from '../../directives/markdown-image-lightbox.directive';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import {
  linkTaskReferencesInHtml,
  markdownToHtml,
  sanitizeHtml,
  type MarkdownImageOptions,
} from '../markdown-utils';

/**
 * Canonical markdown render surface. Replaces the
 * `bypassSecurityTrustHtml(markdownToHtml(...))` + `.markdown-body` +
 * `appMarkdownLightbox` boilerplate that every host used to repeat.
 *
 * Two input paths:
 *   [source]  raw markdown -> client-side rendering via markdown-utils
 *   [html]    pre-rendered HTML string (F22 backend projection) -> sanitised
 *             and embedded as-is
 *
 * Both paths produce the same `.markdown-body` container, so styling and
 * lightbox behaviour stay identical regardless of which side did the
 * render. When both inputs are set, [html] wins so server output is
 * preferred once F22 lands per-job.
 *
 * The host background stays transparent; consumers paint the surrounding
 * surface (chat bubble, prompt-history card, info-button modal). The
 * grey-on-grey "layer around headings" regression came from per-host
 * background drift on the inline markdown div; centralising the wrapper
 * here fixes it once.
 *
 * Canonical contract (this is the single markdown render surface):
 *   - Selector is `app-markdown` (file: markdown-view.component.ts). No
 *     other component may call `marked()` / `markdownToHtml()` or bind
 *     `[innerHTML]` for *markdown* output — route prose through here so
 *     sanitisation, the `.markdown-body` typography (light + dark), the
 *     image lightbox, and link/code handling stay identical everywhere.
 *   - Non-markdown HTML (syntax-highlighted diffs, search-hit `<b>`
 *     snippets, plain-text-escaped strings) is *not* this component's
 *     job and legitimately stays on its own `[innerHTML]` path.
 *
 * Variants (the surface's "modes"):
 *   - default   full-document prose (task description, lane-info modal,
 *               project steering docs). Heading underlines on.
 *   - [dense]   compact column width (chat, activity-log, prompt-history):
 *               smaller font, tighter rhythm, no h1/h2 underlines.
 *   - [editor]  the markdown-rich-editor live-preview surface: min-height
 *               + caret so the contenteditable doesn't collapse.
 *
 * Input precedence: [html] (pre-rendered, e.g. F22 server projection)
 * wins over [source] (raw markdown). Both end in the same `.markdown-body`
 * container so styling is render-path-independent.
 */
@Component({
  selector: 'app-markdown',
  standalone: true,
  imports: [MarkdownImageLightboxDirective],
  templateUrl: './markdown-view.component.html',
  styleUrl: './markdown-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MarkdownViewComponent {
  /** Raw markdown source. Rendered to HTML via the shared markdown-utils. */
  readonly source = input<string | null | undefined>('');

  /**
   * Already-rendered HTML (e.g. produced server-side by F22). When set,
   * this takes precedence over `source` and is sanitised + embedded
   * without re-running the markdown parser. Lets one component carry
   * both render paths without callers having to switch components.
   */
  readonly html = input<string | null | undefined>(null);

  /**
   * Dense variant for chat-width / activity-log / prompt-history columns:
   * smaller font, tighter heading rhythm, no h1/h2 underlines so the
   * layout doesn't fragment in a narrow column.
   */
  readonly dense = input<boolean>(false);

  /**
   * Editor variant — used by the markdown-rich-editor preview surface.
   * Adds a small min-height so the contenteditable doesn't collapse
   * before the user has typed anything and re-enables the caret.
   */
  readonly editor = input<boolean>(false);

  /** Forwarded to markdown-utils for the chat surface's numbered-code shape. */
  readonly codeLineNumbers = input<boolean>(false);
  readonly codeLineNumberThreshold = input<number | undefined>(undefined);

  /**
   * Optional rewriter for `<img src=...>` URLs. Used by the prompt-history
   * + protocol path so `attachments/foo.png` resolves to the job-folder
   * API URL.
   */
  readonly resolveImageSrc = input<((src: string) => string) | null>(null);

  /** Optional test hook for the inner body div. */
  readonly testId = input<string | null>(null);

  private readonly sanitizer = inject(DomSanitizer);
  private readonly taskReferences = inject(TaskReferenceNavigationService);

  readonly safeHtml = computed<SafeHtml>(() => {
    const references = this.taskReferences.markdownReferences();
    const preRendered = this.html();
    if (typeof preRendered === 'string') {
      return this.sanitizer.bypassSecurityTrustHtml(
        sanitizeHtml(linkTaskReferencesInHtml(preRendered, references)),
      );
    }
    const options: MarkdownImageOptions = { taskReferences: references };
    if (this.codeLineNumbers()) options.codeLineNumbers = true;
    const threshold = this.codeLineNumberThreshold();
    if (threshold != null) options.codeLineNumberThreshold = threshold;
    const resolver = this.resolveImageSrc();
    if (resolver) options.resolveImageSrc = resolver;
    return this.sanitizer.bypassSecurityTrustHtml(
      markdownToHtml(this.source() ?? '', options),
    );
  });

  onMarkdownClick(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const anchor = target.closest<HTMLAnchorElement>('a[data-task-ref="true"][data-task-key]');
    if (!anchor) return;

    event.preventDefault();
    event.stopPropagation();
    this.taskReferences.openTaskKey(anchor.dataset['taskKey']);
  }
}
