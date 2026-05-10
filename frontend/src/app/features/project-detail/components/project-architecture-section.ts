import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDocsService } from '../../../services/project-docs.service';
import { ArchitectureDecisionSummary, ArchitectureOverview } from '../../../models/project-docs.model';
import { markdownToHtml } from '../../../components/markdown-utils';

/**
 * Project-level Architecture section: browse the project's
 * architecture-decisions.md, opening one entry at a time. Lighter
 * than security: read-only viewer with a simple "Comment" affordance
 * that captures local notes (not yet persisted — prototype hook
 * for the future chat-style interaction the user described).
 */
@Component({
  selector: 'app-project-architecture-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-architecture-section.html',
  styleUrl: './project-architecture-section.scss'
})
export class ProjectArchitectureSectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);

  readonly overview = signal<ArchitectureOverview | null>(null);
  readonly loading = signal(false);
  readonly openedId = signal<string | null>(null);
  readonly notes = signal<Record<string, string>>({});

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
  }

  refresh() {
    const p = this.projectName();
    if (!p) return;
    this.loading.set(true);
    this.docs.getArchitectureOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); }
    });
  }

  toggle(id: string) {
    this.openedId.update(curr => curr === id ? null : id);
  }

  statusClass(s: string): 'accepted' | 'superseded' | 'deprecated' | 'other' {
    const k = s.toLowerCase();
    if (k.includes('accept')) return 'accepted';
    if (k.includes('super')) return 'superseded';
    if (k.includes('depr')) return 'deprecated';
    return 'other';
  }

  renderedFor(d: ArchitectureDecisionSummary): string {
    try { return markdownToHtml(d.body); } catch { return d.body; }
  }

  noteFor(id: string): string { return this.notes()[id] ?? ''; }

  onNoteInput(id: string, ev: Event) {
    const v = (ev.target as HTMLTextAreaElement).value;
    this.notes.update(n => ({ ...n, [id]: v }));
  }
}
