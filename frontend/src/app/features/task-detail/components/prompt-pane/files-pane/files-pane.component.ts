import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { TaskService } from '../../../../../services/task.service';
import { MarkdownRichEditorComponent } from '../../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { FileSourceHistoryComponent } from '../../../../../components/file-source-history/file-source-history.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskArtifact, TaskArtifactKind } from '../../../../../models/task.model';
import { generatedFileProvenance } from '../../generated-file-provenance.util';
import { NowTickService } from '../../../../../services/now-tick.service';
import { formatRelativeTime } from '../../../../../services/format.util';
import { AspectJsonCardComponent } from './aspect-json-card/aspect-json-card.component';
import { parseAspectDocument, type AspectDocument } from './aspect-document.model';

/**
 * Files tab body. Renders supported documents directly in the job folder
 * (prompt + aspect verdicts + operator notes + HTML explorations) as a list of
 * cards. The first card is always `prompt.md`; the rest follow the Files-tab
 * sort order produced by the backend.
 *
 * Expand / collapse rules (F48):
 *   - Every card starts in preview mode (first ~12 lines) until the user
 *     expands it. Click anywhere on the header (or the "Show full" link)
 *     to expand to the full markdown.
 *   - Polling may replace the artifact objects or add files without changing
 *     expansion state. State resets only when a different task is opened.
 *
 * Editing rule: only the prompt card is editable. The card flips from
 * the rendered markdown view to {@link MarkdownRichEditorComponent} when
 * the user clicks Edit, and back on save. Aspect / note / other files
 * stay read-only in this scope.
 *
 * Content fetch: the artifact manifest already contains size + mtime
 * but no body. Content is fetched lazily per file through
 * `TaskService.readJobFile` and cached in {@link fileContent}; the prompt
 * body is supplied by the parent (it's already part of `TaskDetail`).
 */
@Component({
  selector: 'app-files-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FileSourceHistoryComponent, MarkdownRichEditorComponent, MarkdownViewComponent, TooltipDirective, AspectJsonCardComponent],
  templateUrl: './files-pane.component.html',
  styleUrl: './files-pane.component.scss',
})
export class FilesPaneComponent {
  private readonly jobs = inject(TaskService);
  private readonly nowTick = inject(NowTickService);
  private readonly sanitizer = inject(DomSanitizer);

  readonly artifacts = input<TaskArtifact[]>([]);
  /** Prefilled body for `prompt.md` so we don't re-fetch what `TaskDetail` already loaded. */
  readonly promptContent = input<string>('');
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly isRunning = input(false);

  readonly save = output<string>();

  /** File names whose card is currently expanded. */
  private readonly expanded = signal<Set<string>>(new Set());
  /** Cached file bodies. `null` marks a load error so the view can render a tidy fallback. */
  private readonly content = signal<Map<string, string | null>>(new Map());
  private readonly loading = signal<Set<string>>(new Set());
  private readonly editingPrompt = signal(false);
  /** Tracks which file names we have already kicked off a fetch for, to avoid duplicate calls. */
  private readonly fetched = new Set<string>();
  /**
   * Memoised parse of structured `aspect-*.json` bodies, keyed by file name.
   * Re-parsed only when the cached raw body changes, so the OnPush template
   * can call {@link aspectDoc} per change-detection without re-running
   * `JSON.parse` every cycle.
   */
  private readonly aspectDocCache = new Map<string, { raw: string; doc: AspectDocument | null }>();
  private readonly htmlDocCache = new Map<string, { raw: string; doc: SafeHtml }>();

  readonly onlyPrompt = computed(() => {
    const list = this.artifacts();
    return list.length === 1 && list[0].kind === 'prompt';
  });

  constructor() {
    // Expansion is user-owned UI state. Artifact polling replaces the input
    // array every 10 seconds, so reset only at the task boundary.
    effect(() => {
      this.jobId();
      this.expanded.set(new Set());
      this.editingPrompt.set(false);
      this.content.set(new Map());
      this.loading.set(new Set());
      this.fetched.clear();
      this.aspectDocCache.clear();
      this.htmlDocCache.clear();
    }, { allowSignalWrites: true });

    // Prefetch content for every non-prompt artifact so previews / expansions
    // are instant. Prompt body is supplied by the parent — no fetch needed.
    effect(() => {
      const list = this.artifacts();
      const jobId = this.jobId();
      const watchPath = this.watchPath() ?? undefined;
      if (!jobId) return;
      for (const a of list) {
        if (a.kind === 'prompt') continue;
        if (this.fetched.has(a.name)) continue;
        this.fetched.add(a.name);
        this.markLoading(a.name, true);
        this.jobs.readJobFile(jobId, a.name, watchPath).subscribe({
          next: (text) => {
            this.setContent(a.name, typeof text === 'string' ? text : '');
            this.markLoading(a.name, false);
          },
          error: () => {
            this.setContent(a.name, null);
            this.markLoading(a.name, false);
          },
        });
      }
    });
  }

  isExpanded(name: string): boolean {
    return this.expanded().has(name);
  }

  toggleExpanded(name: string): void {
    const next = new Set(this.expanded());
    if (next.has(name)) {
      next.delete(name);
      if (this.isPromptFile(name)) this.editingPrompt.set(false);
    } else {
      next.add(name);
    }
    this.expanded.set(next);
  }

  /** Resolves the body for a file. Prompt comes from the input; others come from the cache. */
  bodyFor(file: TaskArtifact): string | null | undefined {
    if (file.kind === 'prompt') return this.promptContent();
    return this.content().get(file.name);
  }

  isLoading(name: string): boolean {
    return this.loading().has(name);
  }

  /** True for a structured `aspect-*.json` artefact (rendered as a card). */
  isAspectJson(file: TaskArtifact): boolean {
    return file.kind === 'aspect' && file.name.toLowerCase().endsWith('.json');
  }

  isHtmlFile(file: TaskArtifact): boolean {
    return /\.html?$/i.test(file.name);
  }

  /**
   * `allow-scripts` powers self-contained interaction in the template iframe.
   * `allow-same-origin` is deliberately omitted so the document receives an
   * opaque origin and cannot inherit Studio's origin or directly read its
   * cookies, storage, or DOM. Network requests still follow normal browser
   * and CORS policy.
   */
  trustedHtmlFor(file: TaskArtifact): SafeHtml | null {
    if (!this.isHtmlFile(file)) return null;
    const raw = this.bodyFor(file);
    if (raw == null) return null;
    const cached = this.htmlDocCache.get(file.name);
    if (cached && cached.raw === raw) return cached.doc;
    const doc = this.sanitizer.bypassSecurityTrustHtml(raw);
    this.htmlDocCache.set(file.name, { raw, doc });
    return doc;
  }

  /**
   * Parsed structured aspect document for an `aspect-*.json` file, or `null`
   * when its body has not loaded yet or is not a valid aspect document (in
   * which case the caller falls back to the markdown renderer). Memoised on
   * the raw body so repeat calls during change detection are cheap.
   */
  aspectDoc(file: TaskArtifact): AspectDocument | null {
    if (!this.isAspectJson(file)) return null;
    const raw = this.bodyFor(file);
    if (raw == null) return null;
    const cached = this.aspectDocCache.get(file.name);
    if (cached && cached.raw === raw) return cached.doc;
    const doc = parseAspectDocument(raw);
    this.aspectDocCache.set(file.name, { raw, doc });
    return doc;
  }

  /** Renders the first ~12 lines of a file's content for the preview block. */
  preview(file: TaskArtifact): string | null {
    const body = this.bodyFor(file);
    if (body == null) return null;
    return this.generatePreview(body);
  }

  private generatePreview(content: string): string {
    const lines = content.split(/\r?\n/).slice(0, 12);
    return lines
      .map((l) => (l.length > 120 ? l.slice(0, 117) + '…' : l))
      .join('\n');
  }

  fileIcon(kind: TaskArtifactKind): string {
    switch (kind) {
      case 'prompt': return '\u{1F4DD}'; // memo
      case 'aspect': return '\u{1F50D}'; // magnifying-glass
      case 'codeReview': return 'CR';
      case 'note':   return '\u{1F4CC}'; // pushpin
      default:       return '\u{1F4C4}'; // page-facing-up
    }
  }

  provenanceFor(file: TaskArtifact) {
    return generatedFileProvenance(file.generation);
  }

  formatBytes(n: number): string {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${Math.round(n / 102.4) / 10} KB`;
    return `${Math.round(n / (1024 * 102.4)) / 10} MB`;
  }

  formatRelative(iso: string): string {
    return formatRelativeTime(iso, this.nowTick.now());
  }

  isPromptFile(name: string): boolean {
    return name === 'prompt.md';
  }

  isEditing(): boolean { return this.editingPrompt(); }

  beginEditPrompt(event: Event): void {
    event.stopPropagation();
    if (this.isRunning()) return;
    this.editingPrompt.set(true);
  }

  cancelEditPrompt(event: Event): void {
    event.stopPropagation();
    this.editingPrompt.set(false);
  }

  onPromptSave(content: string): void {
    this.save.emit(content);
    this.editingPrompt.set(false);
  }

  private setContent(name: string, value: string | null): void {
    const next = new Map(this.content());
    next.set(name, value);
    this.content.set(next);
  }

  private markLoading(name: string, on: boolean): void {
    const next = new Set(this.loading());
    if (on) next.add(name);
    else next.delete(name);
    this.loading.set(next);
  }
}
