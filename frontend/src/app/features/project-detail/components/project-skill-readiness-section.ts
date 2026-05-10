import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

/**
 * Project-level "Check skill readiness" surface (docs/skills-architecture.md
 * "First Product Step"). Renders a section with a single button on the
 * project detail panel; clicking it opens an inline modal that:
 *
 *  - lists the skills the task processor knows about for this project,
 *    split into Standard (suggested) vs Project-specific (selected),
 *  - reports whether the watched project's README / AGENTS exposes the
 *    expected skill lookup section (pass / warning / fail),
 *  - offers a one-click "Create README update task" that queues a normal
 *    task in the project's 2-ready lane.
 *
 * No call here mutates the watched project's source tree directly; the
 * fix path goes through the regular task queue (see
 * SkillReadinessService.CreateFixTask on the backend).
 */

type SkillReadinessStatus = 'pass' | 'warning' | 'fail';

interface SkillReadinessFile {
  relPath: string;
  fullPath: string;
  exists: boolean;
  headingFound: boolean;
}

interface SkillReadinessReport {
  projectName: string;
  status: SkillReadinessStatus;
  summary: string;
  checkedFiles: SkillReadinessFile[];
  matchedFile: string | null;
  heading: string | null;
  matchedPhrases: string[];
  missingPhrases: string[];
}

interface SkillEntry {
  id: string;
  name: string;
  description: string;
  category: 'standard' | 'projectSpecific';
  selection: 'selected' | 'suggested';
  relPath: string;
}

interface SkillCatalog {
  projectName: string;
  standard: SkillEntry[];
  projectSpecific: SkillEntry[];
}

interface SkillReadinessFixTaskResult {
  jobId: string;
  watchPath: string;
  targetState: string;
  title: string;
}

@Component({
  selector: 'app-project-skill-readiness-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-skill-readiness-section">
      <h3>
        <span class="psr__icon">🧰</span>
        Skill readiness
      </h3>

      <p class="proj-detail__hint">
        Standard and project-specific skills live centrally in Agent Software Studio.
        This check confirms the watched project's README / AGENTS exposes the lookup
        section direct CLI sessions need to find them.
      </p>

      <div class="psr__row">
        <button type="button"
                class="psr__check-btn"
                data-testid="project-skill-readiness-open"
                (click)="openModal()">
          Check skill readiness
        </button>
        @if (lastReport(); as r) {
          <span class="psr__badge psr__badge--{{ r.status }}"
                data-testid="project-skill-readiness-badge">
            {{ statusLabel(r.status) }}
          </span>
        }
      </div>
    </section>

    @if (open()) {
      <div class="psr-modal__backdrop"
           data-testid="project-skill-readiness-modal"
           (click)="closeModal()">
        <div class="psr-modal"
             role="dialog"
             aria-label="Skill readiness"
             (click)="$event.stopPropagation()">
          <header class="psr-modal__head">
            <h3>Skill readiness · {{ projectName() }}</h3>
            <button type="button"
                    class="psr-modal__close"
                    data-testid="project-skill-readiness-close"
                    aria-label="Close"
                    (click)="closeModal()">×</button>
          </header>

          <section class="psr-modal__section">
            <h4>README / AGENTS lookup</h4>
            @if (loading()) {
              <p class="psr-modal__empty" data-testid="project-skill-readiness-loading">Checking…</p>
            } @else if (error(); as e) {
              <p class="psr-modal__error" data-testid="project-skill-readiness-error">{{ e }}</p>
            } @else if (report(); as r) {
              <p class="psr-modal__verdict psr-modal__verdict--{{ r.status }}"
                 [attr.data-testid]="'project-skill-readiness-status-' + r.status">
                <strong>{{ statusLabel(r.status) }}</strong> — {{ r.summary }}
              </p>

              <ul class="psr-modal__files" data-testid="project-skill-readiness-files">
                @for (f of r.checkedFiles; track f.relPath) {
                  <li [class.psr-modal__file--match]="f.headingFound"
                      [class.psr-modal__file--missing]="!f.exists">
                    <code>{{ f.relPath }}</code>
                    @if (f.headingFound) {
                      <span class="psr-modal__file-tag psr-modal__file-tag--match">heading found</span>
                    } @else if (!f.exists) {
                      <span class="psr-modal__file-tag psr-modal__file-tag--missing">not present</span>
                    } @else {
                      <span class="psr-modal__file-tag">no heading</span>
                    }
                  </li>
                }
              </ul>

              @if (r.matchedPhrases.length > 0 || r.missingPhrases.length > 0) {
                <p class="psr-modal__phrases">
                  <span>Hits:</span>
                  @for (p of r.matchedPhrases; track p) {
                    <code class="psr-modal__phrase psr-modal__phrase--hit">{{ p }}</code>
                  }
                  @for (p of r.missingPhrases; track p) {
                    <code class="psr-modal__phrase psr-modal__phrase--miss">{{ p }}</code>
                  }
                </p>
              }

              @if (r.status !== 'pass') {
                <div class="psr-modal__fix">
                  <button type="button"
                          class="psr-modal__fix-btn"
                          data-testid="project-skill-readiness-fix"
                          [disabled]="creating()"
                          (click)="createFixTask()">
                    {{ creating() ? 'Queueing…' : 'Create README update task' }}
                  </button>
                  <p class="psr-modal__fix-hint">
                    Queues a normal task in <code>2-ready</code> for this project. The agent updates
                    the README through the regular pipeline. No watched project file is edited from here.
                  </p>
                  @if (createdJobId(); as jid) {
                    <p class="psr-modal__fix-ok"
                       data-testid="project-skill-readiness-fix-ok">
                      Task <code>{{ jid }}</code> queued in <code>2-ready</code>.
                    </p>
                  }
                  @if (createError(); as ce) {
                    <p class="psr-modal__fix-err"
                       data-testid="project-skill-readiness-fix-err">{{ ce }}</p>
                  }
                </div>
              }
            }
          </section>

          <section class="psr-modal__section">
            <h4>Standard skills <span class="psr-modal__sublabel">suggested</span></h4>
            @if (catalog(); as c) {
              @if (c.standard.length === 0) {
                <p class="psr-modal__empty">
                  No standard skills found. The task processor exposes skills under
                  <code>.agents/skills/</code>; this list is empty when the runtime
                  cannot resolve that tree.
                </p>
              } @else {
                <ul class="psr-modal__skills" data-testid="project-skill-readiness-standard">
                  @for (s of c.standard; track s.id) {
                    <li>
                      <span class="psr-modal__skill-name">{{ s.name }}</span>
                      <span class="psr-modal__skill-tag psr-modal__skill-tag--suggested">suggested</span>
                      @if (s.description) {
                        <p class="psr-modal__skill-desc">{{ s.description }}</p>
                      }
                      <code class="psr-modal__skill-path">{{ s.relPath }}</code>
                    </li>
                  }
                </ul>
              }
            }
          </section>

          <section class="psr-modal__section">
            <h4>Project skills <span class="psr-modal__sublabel">selected</span></h4>
            @if (catalog(); as c) {
              @if (c.projectSpecific.length === 0) {
                <p class="psr-modal__empty" data-testid="project-skill-readiness-project-empty">
                  No project-specific skills configured for this project yet.
                </p>
              } @else {
                <ul class="psr-modal__skills" data-testid="project-skill-readiness-project">
                  @for (s of c.projectSpecific; track s.id) {
                    <li>
                      <span class="psr-modal__skill-name">{{ s.name }}</span>
                      <span class="psr-modal__skill-tag psr-modal__skill-tag--selected">selected</span>
                      @if (s.description) {
                        <p class="psr-modal__skill-desc">{{ s.description }}</p>
                      }
                      <code class="psr-modal__skill-path">{{ s.relPath }}</code>
                    </li>
                  }
                </ul>
              }
            }
          </section>
        </div>
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .psr__icon { margin-right: 6px; }
    .psr__row { display: flex; align-items: center; gap: 10px; }
    .psr__check-btn {
      background: rgba(125,211,252,0.15);
      color: #bae6fd;
      border: 1px solid rgba(125,211,252,0.40);
      border-radius: 6px;
      padding: 6px 12px;
      font: inherit;
      font-size: 0.85rem;
      cursor: pointer;
    }
    .psr__check-btn:hover { background: rgba(125,211,252,0.25); }
    .psr__badge {
      font-size: 0.72rem;
      padding: 2px 8px;
      border-radius: 999px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .psr__badge--pass { background: rgba(166,227,161,0.20); color: #a6e3a1; }
    .psr__badge--warning { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .psr__badge--fail { background: rgba(243,139,168,0.20); color: #f38ba8; }

    .psr-modal__backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.55);
      display: flex;
      align-items: flex-start;
      justify-content: center;
      padding: 60px 20px 40px;
      z-index: 1000;
      overflow-y: auto;
    }
    .psr-modal {
      background: #1e1e2e;
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 10px;
      width: min(720px, 100%);
      max-height: calc(100vh - 100px);
      overflow-y: auto;
      box-shadow: 0 20px 60px rgba(0,0,0,0.40);
    }
    .psr-modal__head {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 14px 18px;
      border-bottom: 1px solid rgba(255,255,255,0.08);
    }
    .psr-modal__head h3 { margin: 0; font-size: 1rem; color: #f8fafc; }
    .psr-modal__close {
      margin-left: auto;
      background: none;
      color: rgba(255,255,255,0.55);
      border: none;
      font-size: 1.4rem;
      line-height: 1;
      cursor: pointer;
    }
    .psr-modal__close:hover { color: #fff; }

    .psr-modal__section { padding: 14px 18px; border-bottom: 1px solid rgba(255,255,255,0.06); }
    .psr-modal__section:last-of-type { border-bottom: none; }
    .psr-modal__section h4 {
      margin: 0 0 8px;
      font-size: 0.82rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: rgba(255,255,255,0.65);
    }
    .psr-modal__sublabel {
      margin-left: 8px;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: rgba(255,255,255,0.55);
      font-size: 0.66rem;
      letter-spacing: 0.06em;
    }

    .psr-modal__verdict {
      margin: 0 0 8px;
      padding: 8px 10px;
      border-radius: 6px;
      font-size: 0.86rem;
    }
    .psr-modal__verdict--pass { background: rgba(166,227,161,0.10); color: #a6e3a1; border: 1px solid rgba(166,227,161,0.30); }
    .psr-modal__verdict--warning { background: rgba(249,226,175,0.10); color: #f9e2af; border: 1px solid rgba(249,226,175,0.30); }
    .psr-modal__verdict--fail { background: rgba(243,139,168,0.10); color: #f38ba8; border: 1px solid rgba(243,139,168,0.30); }

    .psr-modal__files { list-style: none; padding: 0; margin: 6px 0; font-size: 0.82rem; }
    .psr-modal__files li {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 4px 0;
    }
    .psr-modal__file-tag {
      font-size: 0.70rem;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: rgba(255,255,255,0.65);
    }
    .psr-modal__file-tag--match { background: rgba(166,227,161,0.20); color: #a6e3a1; }
    .psr-modal__file-tag--missing { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.45); }

    .psr-modal__phrases { font-size: 0.78rem; margin: 6px 0; display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .psr-modal__phrase {
      padding: 1px 6px;
      border-radius: 4px;
      background: rgba(255,255,255,0.06);
      color: rgba(255,255,255,0.70);
      font-size: 0.74rem;
    }
    .psr-modal__phrase--hit { background: rgba(166,227,161,0.18); color: #a6e3a1; }
    .psr-modal__phrase--miss { background: rgba(243,139,168,0.18); color: #f38ba8; text-decoration: line-through; }

    .psr-modal__fix { margin-top: 12px; }
    .psr-modal__fix-btn {
      background: rgba(249,226,175,0.16);
      color: #f9e2af;
      border: 1px solid rgba(249,226,175,0.40);
      border-radius: 6px;
      padding: 6px 12px;
      font: inherit;
      font-size: 0.85rem;
      cursor: pointer;
    }
    .psr-modal__fix-btn:disabled { opacity: 0.55; cursor: not-allowed; }
    .psr-modal__fix-btn:hover:not(:disabled) { background: rgba(249,226,175,0.28); }
    .psr-modal__fix-hint { font-size: 0.78rem; color: rgba(255,255,255,0.55); margin: 6px 0 0; }
    .psr-modal__fix-ok { font-size: 0.82rem; color: #a6e3a1; margin: 6px 0 0; }
    .psr-modal__fix-err { font-size: 0.82rem; color: #f38ba8; margin: 6px 0 0; }

    .psr-modal__skills { list-style: none; padding: 0; margin: 0; }
    .psr-modal__skills li {
      padding: 8px 0;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .psr-modal__skills li:last-child { border-bottom: none; }
    .psr-modal__skill-name { color: #cdd6f4; font-weight: 600; font-size: 0.88rem; }
    .psr-modal__skill-tag {
      margin-left: 8px;
      font-size: 0.66rem;
      padding: 1px 8px;
      border-radius: 999px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .psr-modal__skill-tag--selected { background: rgba(125,211,252,0.20); color: #bae6fd; }
    .psr-modal__skill-tag--suggested { background: rgba(196,181,253,0.20); color: #c4b5fd; }
    .psr-modal__skill-desc { margin: 4px 0 2px; color: rgba(255,255,255,0.70); font-size: 0.80rem; line-height: 1.4; }
    .psr-modal__skill-path { font-size: 0.74rem; color: rgba(255,255,255,0.45); }

    .psr-modal__empty { color: rgba(255,255,255,0.55); font-size: 0.82rem; font-style: italic; margin: 6px 0 0; }
    .psr-modal__error { color: #f38ba8; font-size: 0.82rem; margin: 6px 0 0; }
  `]
})
export class ProjectSkillReadinessSectionComponent {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<SkillReadinessReport | null>(null);
  readonly catalog = signal<SkillCatalog | null>(null);
  readonly creating = signal(false);
  readonly createError = signal<string | null>(null);
  readonly createdJobId = signal<string | null>(null);

  // Mirror of the latest report for the closed-modal status badge.
  readonly lastReport = computed(() => this.report());

  openModal(): void {
    this.open.set(true);
    this.error.set(null);
    this.createError.set(null);
    this.createdJobId.set(null);
    this.fetchAll();
  }

  closeModal(): void {
    this.open.set(false);
  }

  statusLabel(status: SkillReadinessStatus): string {
    switch (status) {
      case 'pass': return 'Pass';
      case 'warning': return 'Warning';
      case 'fail': return 'Fail';
    }
  }

  createFixTask(): void {
    const project = this.projectName();
    this.creating.set(true);
    this.createError.set(null);
    this.createdJobId.set(null);
    this.http.post<SkillReadinessFixTaskResult>(
      `/api/projects/${encodeURIComponent(project)}/skill-readiness/fix-task`,
      {}
    ).subscribe({
      next: (res) => {
        this.creating.set(false);
        this.createdJobId.set(res.jobId);
      },
      error: (err) => {
        this.creating.set(false);
        this.createError.set(err?.error?.error || err?.message || 'Failed to queue task');
      }
    });
  }

  private fetchAll(): void {
    const project = this.projectName();
    this.loading.set(true);
    this.report.set(null);
    this.catalog.set(null);

    this.http.get<SkillReadinessReport>(
      `/api/projects/${encodeURIComponent(project)}/skill-readiness`
    ).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (err) => {
        this.loading.set(false);
        this.error.set(err?.error?.error || err?.message || 'Failed to check skill readiness');
      }
    });

    this.http.get<SkillCatalog>(
      `/api/projects/${encodeURIComponent(project)}/skills`
    ).subscribe({
      next: (c) => this.catalog.set(c),
      error: () => this.catalog.set({ projectName: project, standard: [], projectSpecific: [] })
    });
  }
}
