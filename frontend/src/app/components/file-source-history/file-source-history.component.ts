import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { MarkdownViewComponent } from '@coding-agent/chat/markdown';
import type { TaskFileHistoryEntry, TaskFileSourceScope } from '../../models/task.model';
import { TaskService } from '../../services/task.service';
import { formatDateTimeUtc, formatRelativeTime } from '../../services/format.util';
import { NowTickService } from '../../services/now-tick.service';

type PaneMode = 'file' | 'history';
type LoadState = 'idle' | 'loading' | 'loaded' | 'error';
type DiffLineKind = 'add' | 'del' | 'hunk' | 'ctx';

interface DiffLine {
  kind: DiffLineKind;
  text: string;
}

function splitUnifiedDiff(diff: string): DiffLine[] {
  return diff.replace(/\r\n/g, '\n').split('\n').map((text) => {
    if (text.startsWith('+++') || text.startsWith('---')) return { kind: 'hunk', text };
    if (text.startsWith('@@')) return { kind: 'hunk', text };
    if (text.startsWith('+')) return { kind: 'add', text };
    if (text.startsWith('-')) return { kind: 'del', text };
    return { kind: 'ctx', text };
  });
}

@Component({
  selector: 'app-file-source-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownViewComponent],
  templateUrl: './file-source-history.component.html',
  styleUrl: './file-source-history.component.scss',
})
export class FileSourceHistoryComponent {
  private readonly jobs = inject(TaskService);
  private readonly nowTick = inject(NowTickService);

  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly path = input<string>('');
  readonly content = input<string | null | undefined>(undefined);
  readonly dense = input(false);
  readonly scope = input<TaskFileSourceScope>('auto');
  readonly contentTransform = input<(content: string) => string>((content) => content);
  /** Pane to open on. `'history'` lands directly on the git timeline and loads it eagerly. */
  readonly initialMode = input<PaneMode>('file');

  readonly mode = signal<PaneMode>('file');
  readonly history = signal<TaskFileHistoryEntry[]>([]);
  readonly historyState = signal<LoadState>('idle');
  readonly historyError = signal<string | null>(null);
  readonly selectedSha = signal<string | null>(null);
  readonly versionContent = signal<string | null>(null);
  readonly versionState = signal<LoadState>('idle');
  readonly versionError = signal<string | null>(null);
  readonly diffFrom = signal<string | null>(null);
  readonly diffTo = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly diffState = signal<LoadState>('idle');
  readonly diffError = signal<string | null>(null);

  readonly displayContent = computed(() => {
    const value = this.content();
    return value == null ? value : this.contentTransform()(value);
  });
  readonly selectedEntry = computed(() => this.history().find((entry) => entry.sha === this.selectedSha()) ?? null);
  readonly diffLines = computed<DiffLine[]>(() => splitUnifiedDiff(this.diffText()));

  private readonly resetOnFileChange = effect(() => {
    this.jobId();
    this.watchPath();
    this.path();
    const initial = this.initialMode();
    this.mode.set(initial);
    this.history.set([]);
    this.historyState.set('idle');
    this.historyError.set(null);
    this.selectedSha.set(null);
    this.versionContent.set(null);
    this.versionState.set('idle');
    this.versionError.set(null);
    this.diffFrom.set(null);
    this.diffTo.set(null);
    this.diffText.set('');
    this.diffState.set('idle');
    this.diffError.set(null);
    if (initial === 'history') this.loadHistory();
  }, { allowSignalWrites: true });

  showFile(): void {
    this.mode.set('file');
  }

  showHistory(): void {
    this.mode.set('history');
    if (this.historyState() === 'idle') this.loadHistory();
  }

  selectVersion(sha: string): void {
    this.selectedSha.set(sha || null);
    if (sha) this.loadVersion(sha);
  }

  onVersionSelect(event: Event): void {
    this.selectVersion((event.target as HTMLSelectElement).value);
  }

  onDiffFromSelect(event: Event): void {
    this.diffFrom.set((event.target as HTMLSelectElement).value || null);
    this.loadDiff();
  }

  onDiffToSelect(event: Event): void {
    this.diffTo.set((event.target as HTMLSelectElement).value || null);
    this.loadDiff();
  }

  formatTimestamp(iso: string | null | undefined): string {
    return formatDateTimeUtc(iso);
  }

  formatRelative(iso: string | null | undefined): string {
    return formatRelativeTime(iso, this.nowTick.now());
  }

  shortSha(sha: string | null | undefined): string {
    if (!sha) return '';
    return sha.length > 8 ? sha.slice(0, 8) : sha;
  }

  runLabel(entry: TaskFileHistoryEntry): string {
    return entry.runIndex ? `Run #${entry.runIndex}` : 'Unmapped run';
  }

  versionLabel(entry: TaskFileHistoryEntry): string {
    const run = this.runLabel(entry);
    const at = this.formatTimestamp(entry.at);
    return at ? `${run} - ${at}` : `${run} - ${this.shortSha(entry.sha)}`;
  }

  verdictTone(verdict: string | null | undefined): string {
    const lower = (verdict ?? '').toLowerCase();
    if (lower === 'pass' || lower === 'accept' || lower === 'accepted') return 'pass';
    if (lower === 'concerns' || lower === 'defer' || lower === 'reissue') return 'warn';
    if (lower === 'block' || lower === 'failed' || lower === 'fail') return 'block';
    return 'unknown';
  }

  private loadHistory(): void {
    const jobId = this.jobId();
    if (!jobId || !this.path()) return;
    this.historyState.set('loading');
    this.historyError.set(null);
    this.jobs.getTaskFileHistory(jobId, this.path(), this.watchPath() ?? undefined, this.scope()).subscribe({
      next: (entries) => {
        const list = entries ?? [];
        this.history.set(list);
        this.historyState.set('loaded');
        this.seedSelection(list);
      },
      error: (err) => {
        this.historyError.set(err?.error?.error || err?.message || 'Could not load file history.');
        this.historyState.set('error');
      },
    });
  }

  private seedSelection(list: TaskFileHistoryEntry[]): void {
    const latest = list[0] ?? null;
    const previous = list[1] ?? latest;
    this.selectedSha.set(latest?.sha ?? null);
    this.diffFrom.set(previous?.sha ?? null);
    this.diffTo.set(latest?.sha ?? null);
    if (latest) this.loadVersion(latest.sha);
    this.loadDiff();
  }

  private loadVersion(sha: string): void {
    const jobId = this.jobId();
    if (!jobId || !this.path()) return;
    this.versionState.set('loading');
    this.versionError.set(null);
    this.jobs.readTaskFileAt(jobId, this.path(), sha, this.watchPath() ?? undefined, this.scope()).subscribe({
      next: (text) => {
        this.versionContent.set(this.contentTransform()(text));
        this.versionState.set('loaded');
      },
      error: (err) => {
        this.versionError.set(err?.error?.error || err?.message || 'Could not load this version.');
        this.versionState.set('error');
      },
    });
  }

  private loadDiff(): void {
    const jobId = this.jobId();
    const from = this.diffFrom();
    const to = this.diffTo();
    if (!jobId || !this.path() || !from || !to) return;
    if (from === to) {
      this.diffText.set('');
      this.diffState.set('loaded');
      this.diffError.set(null);
      return;
    }
    this.diffState.set('loading');
    this.diffError.set(null);
    this.jobs.diffTaskFileVersions(jobId, this.path(), from, to, this.watchPath() ?? undefined, this.scope()).subscribe({
      next: (text) => {
        this.diffText.set(text ?? '');
        this.diffState.set('loaded');
      },
      error: (err) => {
        this.diffError.set(err?.error?.error || err?.message || 'Could not load the version diff.');
        this.diffState.set('error');
      },
    });
  }
}
