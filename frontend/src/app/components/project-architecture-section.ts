import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDocsService } from '../services/project-docs.service';
import { ArchitectureDecisionSummary, ArchitectureOverview } from '../models/project-docs.model';
import { markdownToHtml } from './markdown-utils';

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
  template: `
    <section class="proj-detail__group" data-testid="project-architecture-section">
      <h3>
        <span class="parch__icon">🏛️</span>
        Architecture decisions
        @if (overview()?.decisions; as ds) {
          <span class="parch__count">{{ ds.length }}</span>
        }
      </h3>

      @if (loading()) {
        <p class="proj-detail__empty">Loading…</p>
      } @else if (!overview()) {
        <p class="proj-detail__empty">No data.</p>
      } @else if (!overview()!.exists) {
        <p class="proj-detail__empty">
          No <code>docs/architecture-decisions.md</code> found at
          <code>{{ overview()!.sourceFile }}</code>. Create one to start the archive.
        </p>
      } @else if (overview()!.decisions.length === 0) {
        <p class="proj-detail__empty">
          The ADR file exists but no <code>## ADR-NNNN</code> headings were parsed.
        </p>
      } @else {
        <ul class="parch__list" data-testid="project-architecture-list">
          @for (d of overview()!.decisions; track d.id) {
            <li class="parch__item"
                [class.parch__item--active]="openedId() === d.id">
              <button class="parch__btn"
                      (click)="toggle(d.id)"
                      [attr.data-testid]="'project-architecture-' + d.id">
                <span class="parch__id">{{ d.id }}</span>
                <span class="parch__title">{{ d.title }}</span>
                <span class="parch__status"
                      [class.parch__status--accepted]="statusClass(d.status) === 'accepted'"
                      [class.parch__status--super]="statusClass(d.status) === 'superseded'"
                      [class.parch__status--depr]="statusClass(d.status) === 'deprecated'">
                  {{ d.status }}
                </span>
                @if (d.date) {
                  <span class="parch__date">{{ d.date }}</span>
                }
              </button>
              @if (openedId() === d.id) {
                <div class="parch__viewer" data-testid="project-architecture-viewer">
                  <div class="parch__body" [innerHTML]="renderedFor(d)"></div>
                  <details class="parch__notes">
                    <summary>Add a note (local, prototype)</summary>
                    <textarea rows="3"
                              class="parch__notes-area"
                              [value]="noteFor(d.id)"
                              (input)="onNoteInput(d.id, $event)"
                              placeholder="Quick reaction or follow-up to remember. Will be persisted in a later iteration."></textarea>
                  </details>
                </div>
              }
            </li>
          }
        </ul>
        <p class="parch__source">
          Source: <code>{{ overview()!.sourceFile }}</code>
        </p>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .parch__icon { margin-right: 6px; }
    .parch__count {
      display: inline-block;
      margin-left: 8px;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
      font-size: 0.72rem;
      font-weight: 600;
      font-variant-numeric: tabular-nums;
    }

    .parch__list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
    .parch__item { background: none; }
    .parch__btn {
      width: 100%;
      display: grid;
      grid-template-columns: max-content 1fr max-content max-content;
      gap: 12px;
      align-items: baseline;
      padding: 7px 10px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 4px;
      color: #cdd6f4;
      font-size: 0.84rem;
      cursor: pointer;
      text-align: left;
    }
    .parch__btn:hover { background: rgba(255,255,255,0.07); border-color: rgba(255,255,255,0.20); }
    .parch__item--active .parch__btn {
      border-color: rgba(196,181,253,0.55);
      background: rgba(196,181,253,0.08);
    }
    .parch__id { font-family: var(--font-mono, monospace); color: #c4b5fd; font-weight: 600; }
    .parch__title { color: #e2e8f0; }
    .parch__status {
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.05);
      font-size: 0.72rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #cbd5e1;
    }
    .parch__status--accepted { background: rgba(166,227,161,0.15); color: #a6e3a1; }
    .parch__status--super    { background: rgba(249,226,175,0.15); color: #fcd34d; }
    .parch__status--depr     { background: rgba(243,139,168,0.15); color: #fda4af; }
    .parch__date { color: rgba(255,255,255,0.55); font-size: 0.74rem; font-variant-numeric: tabular-nums; }

    .parch__viewer {
      margin-top: 4px;
      padding: 12px 14px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 4px;
    }
    .parch__body {
      color: #cdd6f4;
      font-size: 0.88rem;
      line-height: 1.55;
    }
    .parch__body :global(h1),
    .parch__body :global(h2),
    .parch__body :global(h3) { color: #f8fafc; margin: 0.6em 0 0.3em; }
    .parch__body :global(p)  { margin: 0.4em 0; }
    .parch__body :global(strong) { color: #f8fafc; }
    .parch__body :global(code) { background: rgba(255,255,255,0.06); padding: 1px 4px; border-radius: 3px; }

    .parch__notes { margin-top: 10px; }
    .parch__notes summary { cursor: pointer; color: rgba(255,255,255,0.55); font-size: 0.78rem; }
    .parch__notes-area {
      margin-top: 6px;
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.40);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 4px;
      padding: 6px 8px;
      font-family: inherit;
      font-size: 0.84rem;
      resize: vertical;
    }

    .parch__source { margin: 10px 0 0; color: rgba(255,255,255,0.45); font-size: 0.74rem; }
    .parch__source code { color: #c4b5fd; }
  `]
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
