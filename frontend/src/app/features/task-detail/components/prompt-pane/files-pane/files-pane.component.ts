import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TaskService } from '../../../../../services/task.service';
import { MarkdownRichEditorComponent } from '../../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MarkdownViewComponent } from '../../../../../components/markdown-view/markdown-view.component';
import { TooltipDirective } from '../../../../../components/tooltip';
import type { TaskArtifact, TaskArtifactKind } from '../../../../../models/task.model';

/**
 * Files tab body. Renders every `.md` file directly in the job folder
 * (prompt + aspect verdicts + operator notes + anything else) as a list
 * of cards. The first card is always `prompt.md`; the rest follow the
 * Files-tab sort order produced by the backend.
 *
 * Expand / collapse rules (F48):
 *   - Only prompt.md present → expand by default + show a hint that more
 *     files can be dropped into the folder.
 *   - Multiple files → every card stays in preview mode (first ~12 lines)
 *     until the user expands it. Click anywhere on the header (or the
 *     "Show full" link) to expand to the full markdown.
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
  imports: [MarkdownRichEditorComponent, MarkdownViewComponent, TooltipDirective],
  templateUrl: './files-pane.component.html',
  styleUrl: './files-pane.component.scss',
})
export class FilesPaneComponent {
  private readonly jobs = inject(TaskService);

  readonly artifacts = input<TaskArtifact[]>([]);
  /** Prefilled body for `prompt.md` so we don't re-fetch what `TaskDetail` already loaded. */
  readonly promptContent = input<string>('');
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly isRunning = input(false);

  readonly save = output<string>();

  /** Slugs whose card is currently expanded. Multi-file default is empty (preview mode). */
  private readonly expanded = signal<Set<string>>(new Set());
  /** Cached file bodies. `null` marks a load error so the view can render a tidy fallback. */
  private readonly content = signal<Map<string, string | null>>(new Map());
  private readonly loading = signal<Set<string>>(new Set());
  private readonly editingPrompt = signal(false);
  /** Tracks which file names we have already kicked off a fetch for, to avoid duplicate calls. */
  private readonly fetched = new Set<string>();

  readonly onlyPrompt = computed(() => {
    const list = this.artifacts();
    return list.length === 1 && list[0].kind === 'prompt';
  });

  constructor() {
    // Auto-expand the prompt when it's the only artifact. Multi-file lists
    // intentionally start fully collapsed (preview is the at-a-glance view).
    effect(() => {
      const list = this.artifacts();
      const next = new Set<string>();
      if (list.length === 1) {
        next.add(list[0].name);
      }
      this.expanded.set(next);
      // Reset editor state whenever the artifact list changes (new job opened).
      this.editingPrompt.set(false);
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
      case 'note':   return '\u{1F4CC}'; // pushpin
      default:       return '\u{1F4C4}'; // page-facing-up
    }
  }

  formatBytes(n: number): string {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${Math.round(n / 102.4) / 10} KB`;
    return `${Math.round(n / (1024 * 102.4)) / 10} MB`;
  }

  formatRelative(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
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
