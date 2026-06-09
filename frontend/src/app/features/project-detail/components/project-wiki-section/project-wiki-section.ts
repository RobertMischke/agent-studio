import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { WikiFileEntry, WikiOverview } from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { resolveWikiImageSrc } from './wiki-image-resolver';

interface WikiGroup {
  /** Directory path relative to docs root; '' for root-level files. */
  dir: string;
  /** Display label ('/' for the root group). */
  label: string;
  files: WikiFileEntry[];
}

/**
 * Project-level Wiki view: read-only browser over the project's `docs/`
 * tree (navigation card + domain docs + accumulated learnings from the
 * wiki post-processing step). Folder-grouped index on the left, single
 * rendered document on the right. Relative image/diagram references in a
 * doc resolve to the backend wiki-asset endpoint so they render in place.
 *
 * Extends the existing project-docs mechanic (ProjectDocsService /
 * MapProjectDocsEndpoints) — no new storage model.
 */
@Component({
  selector: 'app-project-wiki-section',
  standalone: true,
  imports: [FormsModule, MarkdownViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-wiki-section.html',
  styleUrl: './project-wiki-section.scss',
})
export class ProjectWikiSectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);

  readonly overview = signal<WikiOverview | null>(null);
  readonly loading = signal(false);
  readonly filter = signal('');
  readonly openedRel = signal<string | null>(null);
  readonly openedContent = signal<string>('');
  readonly loadingDoc = signal(false);

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
  }

  readonly filteredFiles = computed<WikiFileEntry[]>(() => {
    const files = this.overview()?.files ?? [];
    const needle = this.filter().trim().toLowerCase();
    if (!needle) return files;
    return files.filter(f =>
      f.relPath.toLowerCase().includes(needle) || f.title.toLowerCase().includes(needle));
  });

  readonly groups = computed<WikiGroup[]>(() => {
    const byDir = new Map<string, WikiFileEntry[]>();
    for (const f of this.filteredFiles()) {
      const slash = f.relPath.lastIndexOf('/');
      const dir = slash >= 0 ? f.relPath.slice(0, slash) : '';
      (byDir.get(dir) ?? byDir.set(dir, []).get(dir)!).push(f);
    }
    return [...byDir.entries()]
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([dir, files]) => ({ dir, label: dir || '/', files }));
  });

  readonly docCount = computed(() => this.overview()?.files.length ?? 0);

  /** Image resolver bound to the currently opened doc's folder. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const project = this.projectName();
    const rel = this.openedRel();
    if (!rel) return (s: string) => s;
    return (s: string) => resolveWikiImageSrc(s, rel, a => this.docs.wikiAssetUrl(project, a));
  });

  refresh(): void {
    const p = this.projectName();
    if (!p) return;
    this.loading.set(true);
    this.docs.getWikiOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  openFile(rel: string): void {
    this.openedRel.set(rel);
    this.openedContent.set('');
    this.loadingDoc.set(true);
    this.docs.getWikiFile(this.projectName(), rel).subscribe({
      next: r => {
        this.openedContent.set(r.content);
        this.loadingDoc.set(false);
      },
      error: () => {
        this.openedContent.set('(failed to load)');
        this.loadingDoc.set(false);
      },
    });
  }

  closeFile(): void {
    this.openedRel.set(null);
    this.openedContent.set('');
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try { return new Date(iso).toLocaleDateString(); } catch { return iso; }
  }
}
