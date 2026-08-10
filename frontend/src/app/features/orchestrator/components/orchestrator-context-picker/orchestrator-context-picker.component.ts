import { ChangeDetectionStrategy, Component, HostListener, computed, inject, input, output, signal } from '@angular/core';
import type { OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  contextSourceId,
  type OrchestratorContextSourceOption,
} from '../../models/orchestrator-context-source.model';
import {
  OrchestratorContextSourceService,
  type OrchestratorContextSourceSearchResult,
} from '../../services/orchestrator-context-source.service';

const EMPTY_RESULTS: OrchestratorContextSourceSearchResult = {
  tasks: [], wiki: [], files: [], commits: [], degraded: false,
};

@Component({
  selector: 'app-orchestrator-context-picker',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-context-picker.component.html',
  styleUrl: './orchestrator-context-picker.component.scss',
})
export class OrchestratorContextPickerComponent implements OnDestroy {
  private readonly sources = inject(OrchestratorContextSourceService);
  readonly project = input.required<string>();
  readonly automaticLabel = input.required<string>();
  readonly automaticIncluded = input(true);
  readonly currentSource = input<OrchestratorContextSourceOption | null>(null);
  readonly attachments = input<readonly OrchestratorContextSourceOption[]>([]);
  readonly disabled = input(false);
  readonly attachmentAdded = output<OrchestratorContextSourceOption>();
  readonly attachmentRemoved = output<string>();
  readonly automaticIncludedChange = output<boolean>();

  readonly open = signal(false);
  readonly query = signal('');
  readonly loading = signal(false);
  readonly results = signal<OrchestratorContextSourceSearchResult>(EMPTY_RESULTS);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private requestVersion = 0;

  readonly selectedIds = computed(() => new Set(this.attachments().map(item => item.id)));
  readonly estimatedTokens = computed(() =>
    (this.automaticIncluded() ? 1_600 : 0)
      + this.attachments().reduce((sum, item) => sum + item.estimateTokens, 0));
  readonly sourceCount = computed(() => (this.automaticIncluded() ? 1 : 0) + this.attachments().length);
  readonly groups = computed(() => [
    { id: 'tasks', label: 'Tasks', items: this.results().tasks },
    { id: 'wiki', label: 'Wiki and Workbenches', items: this.results().wiki },
    { id: 'files', label: 'Files', items: this.results().files },
    { id: 'commits', label: 'Commits', items: this.results().commits },
  ] as const);
  readonly hasResults = computed(() => this.groups().some(group => group.items.length > 0));

  show(): void {
    if (!this.disabled()) this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }

  toggleAutomatic(): void {
    this.automaticIncludedChange.emit(!this.automaticIncluded());
  }

  add(source: OrchestratorContextSourceOption): void {
    if (this.selectedIds().has(source.id)) return;
    this.attachmentAdded.emit(source);
  }

  addDiff(source: OrchestratorContextSourceOption): void {
    this.add(this.diffSource(source));
  }

  remove(id: string): void {
    this.attachmentRemoved.emit(id);
  }

  categoryLabel(source: OrchestratorContextSourceOption): string {
    if (source.reference.kind === 'diff') return 'Diff';
    if (source.category === 'tasks') return 'Task';
    if (source.category === 'commits') return 'Commit';
    if (source.category === 'files') return 'File';
    return 'Page';
  }

  diffSource(source: OrchestratorContextSourceOption): OrchestratorContextSourceOption {
    const reference = {
      ...source.reference,
      kind: 'diff' as const,
      path: null,
      lineRanges: null,
    };
    return {
      ...source,
      id: contextSourceId(reference),
      reference,
    };
  }

  onQuery(value: string): void {
    this.query.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    const query = value.trim();
    if (query.length < 2) {
      this.loading.set(false);
      this.results.set(EMPTY_RESULTS);
      return;
    }
    this.loading.set(true);
    const version = ++this.requestVersion;
    this.searchTimer = setTimeout(() => this.sources.search(this.project(), query).subscribe({
      next: result => {
        if (version !== this.requestVersion) return;
        this.results.set(result);
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.results.set({ ...EMPTY_RESULTS, degraded: true });
        this.loading.set(false);
      },
    }), 180);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.requestVersion += 1;
  }
}
