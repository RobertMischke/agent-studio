import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ProjectDocsService } from '../../../services/project-docs.service';
import { SecurityMeta, SecurityOverview } from '../../../models/project-docs.model';
import { markdownToHtml } from '../../../components/markdown-utils';

/**
 * Project-level Security section: meta header (last review, rating,
 * one-line summary), MD file browser, inline view + edit.
 *
 * Prototype: no diff view, no syntax highlight, no Markdown preview
 * toolbar. Source-mode textarea for editing, rendered Markdown in
 * read mode. Files live under <repoRoot>/docs/security/.
 */
@Component({
  selector: 'app-project-security-section',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-security-section">
      <h3>
        <span class="psec__icon">🔒</span>
        Security
        @if (overview()?.meta; as m) {
          @if (m.rating) {
            <span class="psec__pill"
                  [class.psec__pill--good]="ratingClass(m.rating) === 'good'"
                  [class.psec__pill--warn]="ratingClass(m.rating) === 'warn'"
                  [class.psec__pill--bad]="ratingClass(m.rating) === 'bad'"
                  data-testid="project-security-rating">{{ m.rating }}</span>
          }
        }
      </h3>

      @if (loading()) {
        <p class="proj-detail__empty">Loading…</p>
      } @else if (!overview()) {
        <p class="proj-detail__empty">No data.</p>
      } @else {
        <div class="psec__meta" data-testid="project-security-meta">
          <div>
            <span class="psec__meta-label">Last review</span>
            <span class="psec__meta-value">{{ overview()!.meta.lastReviewDate || '— never recorded —' }}</span>
          </div>
          @if (overview()!.meta.summary) {
            <p class="psec__meta-summary">{{ overview()!.meta.summary }}</p>
          }
          <details class="psec__meta-edit">
            <summary>Edit assessment</summary>
            <div class="psec__meta-form">
              <label>Last review (YYYY-MM-DD)
                <input type="text" [(ngModel)]="metaDraft.lastReviewDate" placeholder="2026-05-03">
              </label>
              <label>Rating
                <select [(ngModel)]="metaDraft.rating">
                  <option [ngValue]="null">— none —</option>
                  <option value="Baseline OK">Baseline OK</option>
                  <option value="Needs review">Needs review</option>
                  <option value="At risk">At risk</option>
                </select>
              </label>
              <label>Summary
                <textarea rows="2" [(ngModel)]="metaDraft.summary"
                          placeholder="One-line take. Why is the project where it is, security-wise?"></textarea>
              </label>
              <button class="psec__meta-save" (click)="saveMeta()">Save</button>
            </div>
          </details>
        </div>

        @if (overview()!.files.length === 0) {
          <p class="proj-detail__empty">
            No documents yet. Files live under
            <code>{{ overview()!.baseDir }}</code>.
            Use “New document” to start the archive.
          </p>
        } @else {
          <ul class="psec__files" data-testid="project-security-files">
            @for (f of overview()!.files; track f.relPath) {
              <li class="psec__file"
                  [class.psec__file--active]="openedRel() === f.relPath">
                <button class="psec__file-btn"
                        (click)="openFile(f.relPath)"
                        [attr.data-testid]="'project-security-file-' + f.relPath">
                  <span class="psec__file-name">{{ f.relPath }}</span>
                  <span class="psec__file-ts">{{ formatTime(f.updatedAt) }}</span>
                </button>
              </li>
            }
          </ul>
        }

        <div class="psec__new">
          <button class="psec__new-btn" (click)="startNewFile()" data-testid="project-security-new">
            ＋ New document
          </button>
          @if (newFileMode()) {
            <div class="psec__new-form">
              <input type="text" [(ngModel)]="newFileName"
                     placeholder="reviews/2026-05-03-baseline.md"
                     data-testid="project-security-new-name">
              <button (click)="createNewFile()" data-testid="project-security-new-create">Create</button>
              <button (click)="cancelNewFile()">Cancel</button>
            </div>
          }
        </div>

        @if (openedRel(); as rel) {
          <div class="psec__viewer" data-testid="project-security-viewer">
            <header class="psec__viewer-head">
              <code>{{ rel }}</code>
              <div class="psec__viewer-actions">
                @if (!editing()) {
                  <button (click)="startEdit()" data-testid="project-security-edit">Edit</button>
                } @else {
                  <button (click)="saveEdit()" data-testid="project-security-save">Save</button>
                  <button (click)="cancelEdit()">Cancel</button>
                }
                <button (click)="closeFile()">Close</button>
              </div>
            </header>
            @if (!editing()) {
              <div class="psec__viewer-body" [innerHTML]="renderedHtml()"></div>
            } @else {
              <textarea class="psec__viewer-editor"
                        rows="18"
                        [(ngModel)]="editorDraft"
                        data-testid="project-security-editor"></textarea>
            }
          </div>
        }
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .psec__icon { margin-right: 6px; }
    .psec__pill {
      display: inline-block;
      margin-left: 8px;
      padding: 1px 8px;
      border-radius: 999px;
      font-size: 0.72rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
      border: 1px solid rgba(255,255,255,0.14);
    }
    .psec__pill--good { background: rgba(166,227,161,0.15); color: #a6e3a1; border-color: rgba(166,227,161,0.40); }
    .psec__pill--warn { background: rgba(249,226,175,0.15); color: #fcd34d; border-color: rgba(249,226,175,0.40); }
    .psec__pill--bad  { background: rgba(243,139,168,0.15); color: #fda4af; border-color: rgba(243,139,168,0.45); }

    .psec__meta {
      margin-bottom: 12px;
      padding: 10px 12px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 6px;
      font-size: 0.85rem;
    }
    .psec__meta-label { color: rgba(255,255,255,0.55); margin-right: 6px; font-size: 0.78rem; }
    .psec__meta-value { color: #cdd6f4; font-variant-numeric: tabular-nums; }
    .psec__meta-summary { margin: 6px 0 0; color: #e2e8f0; font-size: 0.85rem; line-height: 1.5; }
    .psec__meta-edit summary { cursor: pointer; color: rgba(255,255,255,0.55); font-size: 0.78rem; margin-top: 6px; }
    .psec__meta-form { display: flex; flex-direction: column; gap: 6px; margin-top: 8px; }
    .psec__meta-form label { display: flex; flex-direction: column; gap: 2px; font-size: 0.78rem; color: rgba(255,255,255,0.65); }
    .psec__meta-form input, .psec__meta-form select, .psec__meta-form textarea {
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 4px;
      padding: 4px 6px;
      font-size: 0.85rem;
      font-family: inherit;
    }
    .psec__meta-save {
      align-self: flex-start;
      margin-top: 4px;
      background: rgba(137, 180, 250, 0.18);
      color: #89b4fa;
      border: 1px solid rgba(137, 180, 250, 0.45);
      border-radius: 4px;
      padding: 4px 12px;
      font-size: 0.80rem;
      cursor: pointer;
    }

    .psec__files { list-style: none; padding: 0; margin: 8px 0 6px; display: flex; flex-direction: column; gap: 4px; }
    .psec__file-btn {
      width: 100%;
      display: flex;
      gap: 12px;
      align-items: baseline;
      padding: 6px 10px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 4px;
      color: #cdd6f4;
      font-size: 0.82rem;
      cursor: pointer;
      text-align: left;
    }
    .psec__file-btn:hover { background: rgba(255,255,255,0.07); border-color: rgba(255,255,255,0.20); }
    .psec__file--active .psec__file-btn { border-color: rgba(137,180,250,0.55); background: rgba(137,180,250,0.10); }
    .psec__file-name { flex: 1; font-family: var(--font-mono, monospace); }
    .psec__file-ts { color: rgba(255,255,255,0.45); font-size: 0.74rem; font-variant-numeric: tabular-nums; }

    .psec__new { margin-top: 8px; display: flex; flex-direction: column; gap: 6px; }
    .psec__new-btn {
      align-self: flex-start;
      background: rgba(255,255,255,0.04);
      color: #cdd6f4;
      border: 1px dashed rgba(255,255,255,0.20);
      border-radius: 4px;
      padding: 4px 10px;
      font-size: 0.80rem;
      cursor: pointer;
    }
    .psec__new-form { display: flex; gap: 6px; flex-wrap: wrap; }
    .psec__new-form input {
      flex: 1;
      min-width: 220px;
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 4px;
      padding: 4px 6px;
      font-family: var(--font-mono, monospace);
      font-size: 0.82rem;
    }
    .psec__new-form button {
      background: rgba(137,180,250,0.18);
      color: #89b4fa;
      border: 1px solid rgba(137,180,250,0.45);
      border-radius: 4px;
      padding: 4px 10px;
      font-size: 0.80rem;
      cursor: pointer;
    }

    .psec__viewer {
      margin-top: 12px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      overflow: hidden;
    }
    .psec__viewer-head {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 10px;
      background: rgba(255,255,255,0.05);
      border-bottom: 1px solid rgba(255,255,255,0.08);
      font-size: 0.78rem;
    }
    .psec__viewer-head code { color: #c4b5fd; flex: 1; }
    .psec__viewer-actions { display: flex; gap: 4px; }
    .psec__viewer-actions button {
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 4px;
      padding: 2px 10px;
      font-size: 0.76rem;
      cursor: pointer;
    }
    .psec__viewer-body {
      padding: 12px 14px;
      color: #cdd6f4;
      font-size: 0.88rem;
      line-height: 1.55;
    }
    .psec__viewer-body :global(h1),
    .psec__viewer-body :global(h2),
    .psec__viewer-body :global(h3) { color: #f8fafc; margin: 0.6em 0 0.3em; }
    .psec__viewer-body :global(code) { background: rgba(255,255,255,0.06); padding: 1px 4px; border-radius: 3px; }
    .psec__viewer-body :global(pre) {
      background: rgba(0,0,0,0.45);
      padding: 8px 10px;
      border-radius: 4px;
      overflow-x: auto;
    }
    .psec__viewer-editor {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.40);
      color: #cdd6f4;
      border: none;
      padding: 12px 14px;
      font-family: var(--font-mono, monospace);
      font-size: 0.84rem;
      line-height: 1.5;
      resize: vertical;
    }
  `]
})
export class ProjectSecuritySectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);

  readonly overview = signal<SecurityOverview | null>(null);
  readonly loading = signal(false);
  readonly openedRel = signal<string | null>(null);
  readonly openedContent = signal<string>('');
  readonly editing = signal(false);
  readonly newFileMode = signal(false);

  newFileName = '';
  editorDraft = '';
  metaDraft: SecurityMeta = { lastReviewDate: null, rating: null, summary: null };

  readonly renderedHtml = computed(() => {
    const c = this.openedContent();
    if (!c) return '';
    try { return markdownToHtml(c); } catch { return c; }
  });

  constructor() {
    // Auto-load whenever the project input changes.
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
  }

  refresh() {
    const p = this.projectName();
    if (!p) return;
    this.loading.set(true);
    this.docs.getSecurityOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.metaDraft = { ...ov.meta };
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); }
    });
  }

  ratingClass(r: string | null): 'good' | 'warn' | 'bad' | 'neutral' {
    if (!r) return 'neutral';
    const s = r.toLowerCase();
    if (s.includes('ok') || s.includes('good') || s.includes('baseline')) return 'good';
    if (s.includes('risk') || s.includes('bad') || s.includes('fail')) return 'bad';
    if (s.includes('review') || s.includes('warn') || s.includes('todo')) return 'warn';
    return 'neutral';
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try { return new Date(iso).toLocaleDateString(); } catch { return iso; }
  }

  openFile(rel: string) {
    this.editing.set(false);
    this.openedRel.set(rel);
    this.openedContent.set('');
    this.docs.getSecurityFile(this.projectName(), rel).subscribe({
      next: r => this.openedContent.set(r.content),
      error: () => this.openedContent.set('(failed to load)')
    });
  }

  closeFile() {
    this.openedRel.set(null);
    this.openedContent.set('');
    this.editing.set(false);
  }

  startEdit() {
    this.editorDraft = this.openedContent();
    this.editing.set(true);
  }

  cancelEdit() {
    this.editing.set(false);
    this.editorDraft = '';
  }

  saveEdit() {
    const rel = this.openedRel();
    if (!rel) return;
    this.docs.putSecurityFile(this.projectName(), rel, this.editorDraft).subscribe({
      next: () => {
        this.openedContent.set(this.editorDraft);
        this.editing.set(false);
        this.refresh();
      },
      error: () => {}
    });
  }

  startNewFile() {
    this.newFileMode.set(true);
    this.newFileName = 'overview.md';
  }
  cancelNewFile() {
    this.newFileMode.set(false);
    this.newFileName = '';
  }
  createNewFile() {
    let name = (this.newFileName || '').trim();
    if (!name) return;
    if (!name.toLowerCase().endsWith('.md')) name += '.md';
    const seed = `# ${name.replace(/\.md$/i, '')}\n\nWrite the situation, the requirements, and any relevant history here.\n`;
    this.docs.putSecurityFile(this.projectName(), name, seed).subscribe({
      next: () => {
        this.newFileMode.set(false);
        this.newFileName = '';
        this.refresh();
        this.openFile(name);
      },
      error: () => {}
    });
  }

  saveMeta() {
    this.docs.putSecurityMeta(this.projectName(), this.metaDraft).subscribe({
      next: () => this.refresh(),
      error: () => {}
    });
  }
}
