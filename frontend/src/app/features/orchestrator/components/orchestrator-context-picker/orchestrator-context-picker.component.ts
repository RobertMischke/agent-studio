import { HttpClient, HttpParams } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { OrchestratorContextReference } from '../../models/orchestrator.model';

interface KnownSourceItem {
  domain: 'commits' | 'files';
  projectName: string;
  title: string;
  subtitle: string;
  sha?: string;
  path?: string;
  repositoryId?: string;
  revision?: string;
}

interface KnownSourceResponse {
  commits: KnownSourceItem[];
  files: KnownSourceItem[];
  errors: Record<string, string>;
}

@Component({
  selector: 'app-orchestrator-context-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-context-picker.component.html',
  styleUrl: './orchestrator-context-picker.component.scss',
})
export class OrchestratorContextPickerComponent implements OnDestroy {
  private readonly http = inject(HttpClient);

  readonly projectId = input<string | null>(null);
  readonly currentReference = input<OrchestratorContextReference | null>(null);
  readonly references = signal<OrchestratorContextReference[]>([]);

  readonly open = signal(false);
  readonly query = signal('');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly files = signal<KnownSourceItem[]>([]);
  readonly commits = signal<KnownSourceItem[]>([]);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private requestVersion = 0;
  private referenceProject: string | null = null;

  private readonly resetReferencesForProject = effect(() => {
    const project = this.projectId();
    untracked(() => {
      if (project === this.referenceProject) return;
      this.referenceProject = project;
      this.clear();
    });
  });

  readonly sourceCountLabel = computed(() => {
    const count = this.references().length;
    return `${count} ${count === 1 ? 'reference' : 'references'}`;
  });

  toggle(): void {
    this.open.update(value => !value);
    if (!this.open()) this.resetSearch();
  }

  onQuery(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.query.set(value);
    if (this.timer) clearTimeout(this.timer);
    const query = value.trim();
    const version = ++this.requestVersion;
    if (query.length < 2 || !this.projectId()) {
      this.files.set([]);
      this.commits.set([]);
      this.loading.set(false);
      this.error.set(null);
      return;
    }
    this.loading.set(true);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.search(query, version);
    }, 120);
  }

  add(reference: OrchestratorContextReference): void {
    if (this.isSelected(reference)) return;
    this.references.update(references => [...references, this.copy(reference)]);
  }

  remove(reference: OrchestratorContextReference): void {
    const key = contextReferenceKey(reference);
    this.references.update(references => references.filter(item => contextReferenceKey(item) !== key));
  }

  snapshot(): OrchestratorContextReference[] {
    return this.references().map(reference => this.copy(reference));
  }

  clear(): void {
    this.references.set([]);
    this.open.set(false);
    this.resetSearch();
  }

  addFile(item: KnownSourceItem): void {
    if (!item.path) return;
    this.add({
      kind: 'repository-file',
      reference: item.path,
      projectId: item.projectName,
      repositoryId: item.repositoryId ?? item.projectName,
      revision: item.revision,
    });
  }

  addCommit(item: KnownSourceItem, kind: 'commit' | 'diff'): void {
    if (!item.sha) return;
    this.add({
      kind,
      reference: item.sha,
      projectId: item.projectName,
      repositoryId: item.repositoryId ?? item.projectName,
      revision: item.revision ?? item.sha,
    });
  }

  isSelected(reference: OrchestratorContextReference): boolean {
    const key = contextReferenceKey(reference);
    return this.references().some(item => contextReferenceKey(item) === key);
  }

  label(reference: OrchestratorContextReference): string {
    const path = reference.path || reference.reference;
    const range = reference.lineRanges?.map(item => `L${item.startLine}-L${item.endLine}`).join(', ');
    return `${reference.kind}: ${path}${range ? ` · ${range}` : ''}`;
  }

  trackItem(item: KnownSourceItem): string {
    return `${item.domain}:${item.projectName}:${item.sha ?? item.path ?? item.subtitle}`;
  }

  ngOnDestroy(): void {
    if (this.timer) clearTimeout(this.timer);
  }

  private search(query: string, version: number): void {
    const params = new HttpParams()
      .set('q', query)
      .set('domains', 'commits,files')
      .set('limit', 20);
    this.http.get<KnownSourceResponse>('/api/search', { params }).subscribe({
      next: response => {
        if (version !== this.requestVersion) return;
        const project = this.projectId();
        this.files.set((response.files ?? []).filter(item => item.projectName === project));
        this.commits.set((response.commits ?? []).filter(item => item.projectName === project));
        this.error.set(response.errors?.['commits'] || response.errors?.['files'] || null);
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.files.set([]);
        this.commits.set([]);
        this.error.set('Known sources are temporarily unavailable.');
        this.loading.set(false);
      },
    });
  }

  private resetSearch(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.requestVersion++;
    this.query.set('');
    this.files.set([]);
    this.commits.set([]);
    this.error.set(null);
    this.loading.set(false);
  }

  private copy(reference: OrchestratorContextReference): OrchestratorContextReference {
    return {
      ...reference,
      lineRanges: reference.lineRanges?.map(range => ({ ...range })),
    };
  }
}

export function contextReferenceKey(reference: OrchestratorContextReference): string {
  const ranges = reference.lineRanges
    ?.map(range => `${range.startLine}-${range.endLine}`)
    .join(',') ?? '';
  return [
    reference.kind,
    reference.projectId ?? '',
    reference.repositoryId ?? '',
    reference.reference,
    reference.path ?? '',
    ranges,
  ].join(':').toLowerCase();
}
