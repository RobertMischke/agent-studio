import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { SteeringDocsService } from '../services/steering-docs.service';
import { JobService } from '../services/job.service';
import {
  SteeringDocsOverview,
  SteeringDocsSource,
  SteeringDocsWarning,
} from '../models/steering-docs.model';
import { markdownToHtml } from './markdown-utils';

/**
 * Project-level Steering Docs surface. Shows the agent-facing
 * instruction sources for a watched project (README, AGENTS, ROADMAP,
 * task contract, skills lookup, ADR archive, runtime prompts, project
 * settings), a small heuristic warning set (missing or stale entries,
 * shim files that have grown past their contract), and explicit action
 * buttons that queue normal 1-preparation tasks: summarize, check
 * drift, analyze recurring failures, propose README / AGENTS update,
 * create generic follow-up.
 *
 * V1 keeps this read-only on disk. The service does not summarize or
 * rewrite docs. Drilling into a source opens its raw Markdown inline so
 * the source-of-truth view stays available alongside the human summary
 * actions.
 */

interface StatusBucket {
  label: string;
  count: number;
  cls: 'present' | 'missing';
}

interface SteeringAction {
  slug: string;
  label: string;
  description: string;
}

const ACTIONS: SteeringAction[] = [
  { slug: 'summarize', label: 'Summarize Steering Docs', description: 'Spawn a task that reads the inventory below and produces a human summary of what agents are currently told.' },
  { slug: 'check-drift', label: 'Check Docs Drift', description: 'Spawn a task that compares the steering files against current code and flags stale rules or contradictions.' },
  { slug: 'analyze-failures', label: 'Analyze Recurring Job Failures', description: 'Spawn a task that scans recent blocked / needs-input outcomes and proposes steering-doc changes.' },
  { slug: 'propose-readme', label: 'Propose README Update', description: 'Spawn a task that drafts a README change for review, evidence-first.' },
  { slug: 'propose-agents', label: 'Propose AGENTS Update', description: 'Spawn a task that drafts an AGENTS.md change for review, evidence-first.' },
  { slug: 'create-followup', label: 'Create Follow-up Task', description: 'Queue a generic follow-up tied to the steering surface for later scoping.' },
];

@Component({
  selector: 'app-project-steering-docs-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-steering-docs-section">
      <h3>
        <span class="psd__icon">🧭</span>
        Steering Docs
        @if (overview(); as ov) {
          <span class="psd__pill" data-testid="project-steering-docs-count">
            {{ presentCount() }} / {{ ov.sources.length }} present
          </span>
          @if (warningCount() > 0) {
            <span class="psd__pill psd__pill--warn" data-testid="project-steering-docs-warning-count">
              {{ warningCount() }} warning{{ warningCount() === 1 ? '' : 's' }}
            </span>
          }
        }
      </h3>

      <p class="proj-detail__hint">
        Inventory of the agent-facing instruction files for this project. Raw sources stay
        visible; the human summary and warnings below help spot stale or missing guidance.
        Action buttons queue normal 1-preparation tasks - the surface never silently rewrites docs.
      </p>

      @if (loading() && !overview() && !error()) {
        <p class="proj-detail__empty" data-testid="project-steering-docs-loading">Loading steering inventory…</p>
      }

      @if (error(); as e) {
        <div class="psd__error" data-testid="project-steering-docs-error">
          <strong>Could not load steering inventory.</strong>
          <span>{{ e }}</span>
          <button class="psd__btn psd__btn--ghost"
                  data-testid="project-steering-docs-retry"
                  (click)="refresh()">Retry</button>
        </div>
      }

      @if (overview(); as ov) {
        @if (ov.sources.length === 0) {
          <p class="proj-detail__empty" data-testid="project-steering-docs-empty">
            No steering inventory available for this project. The base directory could not be resolved
            (no <code>repositoryPath</code>, <code>rootPath</code>, or watch path).
          </p>
        } @else {
          <h4 class="psd__sub">Raw sources</h4>
          <ul class="psd__sources" data-testid="project-steering-docs-sources">
            @for (s of ov.sources; track s.id) {
              <li class="psd__source"
                  [class.psd__source--missing]="!s.exists"
                  [class.psd__source--active]="openedId() === s.id">
                <button class="psd__src-btn"
                        [attr.data-testid]="'project-steering-docs-source-' + s.id"
                        (click)="toggle(s)">
                  <span class="psd__src-row">
                    <span class="psd__src-label">{{ s.label }}</span>
                    <span class="psd__src-path"><code>{{ s.relPath }}</code></span>
                    @if (s.exists) {
                      <span class="psd__src-meta">{{ formatSize(s.size) }} · {{ formatTime(s.updatedAt) }}</span>
                    } @else {
                      <span class="psd__src-meta psd__src-meta--missing"
                            [attr.data-testid]="'project-steering-docs-missing-' + s.id">missing</span>
                    }
                  </span>
                  <span class="psd__src-why">{{ s.why }}</span>
                </button>
                @if (openedId() === s.id) {
                  <div class="psd__viewer" data-testid="project-steering-docs-viewer">
                    @if (!s.exists) {
                      <p class="psd__empty">
                        File is missing at <code>{{ s.relPath }}</code> under <code>{{ ov.baseDir }}</code>.
                      </p>
                    } @else if (s.children && s.children.length > 0) {
                      <p class="psd__hint">
                        Directory listing - click a file to open its content:
                      </p>
                      <ul class="psd__children" data-testid="project-steering-docs-children">
                        @for (c of s.children; track c.relPath) {
                          <li>
                            <button class="psd__child-btn"
                                    [attr.data-testid]="'project-steering-docs-child-' + c.name"
                                    [class.psd__child-btn--active]="openedRel() === c.relPath"
                                    (click)="openChild(c.relPath)">
                              <code>{{ c.name }}</code>
                              <span>{{ formatSize(c.size) }} · {{ formatTime(c.updatedAt) }}</span>
                            </button>
                          </li>
                        }
                      </ul>
                      @if (childContent(); as cc) {
                        <article class="psd__body psd__body--child"
                                 data-testid="project-steering-docs-child-content"
                                 [innerHTML]="renderMarkdown(cc.content)"></article>
                      }
                    } @else if (fileContent(); as fc) {
                      <article class="psd__body"
                               data-testid="project-steering-docs-content"
                               [innerHTML]="renderMarkdown(fc.content)"></article>
                    } @else if (fileLoading()) {
                      <p class="psd__empty">Loading file…</p>
                    } @else if (fileError()) {
                      <p class="psd__empty psd__empty--error" data-testid="project-steering-docs-file-error">{{ fileError() }}</p>
                    }
                  </div>
                }
              </li>
            }
          </ul>

          <h4 class="psd__sub">Human summary</h4>
          <div class="psd__summary" data-testid="project-steering-docs-summary">
            <ul class="psd__bullets">
              <li>
                Agents on this project read
                <strong>{{ presentCount() }}</strong> of
                <strong>{{ ov.sources.length }}</strong> canonical steering sources.
                Latest edit: {{ ov.lastUpdated ? formatTime(ov.lastUpdated) : '— never recorded —' }}.
              </li>
              <li>
                Critical rules and non-goals live in <code>AGENTS.md</code> and
                <code>ROADMAP.md</code>; the durable archive of architectural decisions
                is <code>docs/architecture-decisions.md</code>. Project settings live in
                <code>backend/appsettings.json</code> (defaults) and
                <code>backend/appsettings.Local.json</code> (gitignored overrides).
              </li>
              @if (statusBuckets().length > 0) {
                <li class="psd__buckets">
                  @for (b of statusBuckets(); track b.label) {
                    <span class="psd__bucket"
                          [class.psd__bucket--missing]="b.cls === 'missing'">
                      {{ b.label }} <strong>{{ b.count }}</strong>
                    </span>
                  }
                </li>
              }
              <li class="psd__caveat">
                The summary above is a heuristic projection of the inventory, not a
                rewritten human review. Click <em>Summarize Steering Docs</em> below
                to spawn a task that reads each file and produces a real summary.
              </li>
            </ul>
          </div>

          <h4 class="psd__sub">Warnings</h4>
          @if (ov.warnings.length === 0) {
            <p class="proj-detail__empty" data-testid="project-steering-docs-warnings-empty">
              No drift heuristics tripped: every required source is present, no shim has grown
              past its contract, and no file is older than the staleness threshold.
            </p>
          } @else {
            <ul class="psd__warnings" data-testid="project-steering-docs-warnings">
              @for (w of ov.warnings; track w.message) {
                <li class="psd__warning psd__warning--{{ w.severity }}"
                    [attr.data-testid]="'project-steering-docs-warning-' + w.kind"
                    [class.psd__warning--clickable]="w.evidenceRefs.length > 0"
                    (click)="onWarningClick(w)">
                  <header>
                    <span class="psd__sev psd__sev--{{ w.severity }}">{{ w.severity }}</span>
                    <span class="psd__warn-kind">{{ humanWarningKind(w.kind) }}</span>
                  </header>
                  <p>{{ w.message }}</p>
                  @if (w.evidenceRefs.length > 0) {
                    <footer>
                      <span class="psd__evidence-label">Evidence:</span>
                      @for (ref of w.evidenceRefs; track ref) {
                        <code class="psd__evidence">{{ ref }}</code>
                      }
                    </footer>
                  }
                </li>
              }
            </ul>
          }

          <h4 class="psd__sub">Actions</h4>
          <div class="psd__actions" data-testid="project-steering-docs-actions">
            @for (a of actions; track a.slug) {
              <button class="psd__btn"
                      [attr.data-testid]="'project-steering-docs-action-' + a.slug"
                      [title]="a.description"
                      [disabled]="busyAction() === a.slug"
                      (click)="runAction(a)">
                {{ a.label }}
                @if (busyAction() === a.slug) { <span class="psd__btn-spin">…</span> }
              </button>
            }
          </div>
          @if (actionMessage(); as msg) {
            <p class="psd__action-msg" data-testid="project-steering-docs-action-msg">{{ msg }}</p>
          }
          @if (actionError(); as err) {
            <p class="psd__action-error" data-testid="project-steering-docs-action-error">{{ err }}</p>
          }
        }
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .psd__icon { margin-right: 6px; }
    .psd__pill {
      font-size: 0.7rem;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.10);
      color: #cdd6f4;
      margin-left: 8px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-variant-numeric: tabular-nums;
    }
    .psd__pill--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }

    .psd__sub {
      margin: 16px 0 6px;
      font-size: 0.78rem;
      color: rgba(255,255,255,0.65);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .psd__error {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
      margin: 6px 0;
      padding: 8px 10px;
      border: 1px solid rgba(243,139,168,0.40);
      background: rgba(243,139,168,0.10);
      color: #fda4af;
      border-radius: 6px;
      font-size: 0.85rem;
    }
    .psd__error strong { color: #f38ba8; }

    .psd__sources { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
    .psd__source { background: none; }
    .psd__src-btn {
      width: 100%;
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 10px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 4px;
      color: #cdd6f4;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .psd__src-btn:hover { background: rgba(255,255,255,0.07); border-color: rgba(255,255,255,0.20); }
    .psd__source--missing .psd__src-btn {
      border-color: rgba(249,226,175,0.30);
      background: rgba(249,226,175,0.05);
      color: rgba(255,255,255,0.65);
    }
    .psd__source--active .psd__src-btn {
      border-color: rgba(196,181,253,0.55);
      background: rgba(196,181,253,0.08);
    }
    .psd__src-row {
      display: grid;
      grid-template-columns: max-content 1fr max-content;
      gap: 12px;
      align-items: baseline;
    }
    .psd__src-label { font-weight: 600; color: #f8fafc; font-size: 0.86rem; }
    .psd__src-path code {
      font-family: var(--font-mono, monospace);
      font-size: 0.78rem;
      color: #c4b5fd;
      background: rgba(255,255,255,0.05);
      padding: 1px 4px;
      border-radius: 3px;
    }
    .psd__src-meta {
      color: rgba(255,255,255,0.55);
      font-size: 0.74rem;
      font-variant-numeric: tabular-nums;
    }
    .psd__src-meta--missing {
      color: #f9e2af;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-weight: 600;
    }
    .psd__src-why {
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
      line-height: 1.4;
    }

    .psd__viewer {
      margin-top: 4px;
      padding: 12px 14px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 4px;
    }
    .psd__hint { margin: 0 0 8px; color: rgba(255,255,255,0.55); font-size: 0.78rem; }
    .psd__empty { color: rgba(255,255,255,0.50); font-style: italic; margin: 0; font-size: 0.82rem; }
    .psd__empty--error { color: #fda4af; font-style: normal; }
    .psd__body {
      color: #cdd6f4;
      font-size: 0.86rem;
      line-height: 1.55;
      max-height: 380px;
      overflow: auto;
      white-space: normal;
    }
    .psd__body--child { margin-top: 12px; padding-top: 8px; border-top: 1px dashed rgba(255,255,255,0.10); }

    .psd__children { list-style: none; padding: 0; margin: 0 0 6px; display: flex; flex-direction: column; gap: 4px; }
    .psd__child-btn {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      gap: 12px;
      width: 100%;
      padding: 4px 8px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 4px;
      color: #cdd6f4;
      font: inherit;
      cursor: pointer;
      text-align: left;
    }
    .psd__child-btn:hover { background: rgba(255,255,255,0.07); }
    .psd__child-btn--active { border-color: rgba(196,181,253,0.55); background: rgba(196,181,253,0.08); }
    .psd__child-btn code { color: #c4b5fd; font-size: 0.78rem; }
    .psd__child-btn span { color: rgba(255,255,255,0.55); font-size: 0.72rem; font-variant-numeric: tabular-nums; }

    .psd__summary {
      padding: 10px 12px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 6px;
    }
    .psd__bullets { margin: 0; padding-left: 18px; color: #cdd6f4; font-size: 0.86rem; line-height: 1.55; }
    .psd__bullets li { margin: 0 0 4px; }
    .psd__bullets code { background: rgba(255,255,255,0.06); padding: 1px 4px; border-radius: 3px; font-size: 0.78rem; color: #c4b5fd; }
    .psd__buckets { display: flex; flex-wrap: wrap; gap: 6px; list-style: none; }
    .psd__bucket {
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
      font-size: 0.74rem;
      font-variant-numeric: tabular-nums;
    }
    .psd__bucket--missing { background: rgba(249,226,175,0.16); color: #f9e2af; }
    .psd__caveat { color: rgba(255,255,255,0.55); font-style: italic; }

    .psd__warnings { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; }
    .psd__warning {
      padding: 8px 10px;
      border-left: 2px solid rgba(255,255,255,0.10);
      background: rgba(255,255,255,0.03);
      border-radius: 0 4px 4px 0;
    }
    .psd__warning--info { border-left-color: rgba(148,163,184,0.45); }
    .psd__warning--warn { border-left-color: #f9e2af; background: rgba(249,226,175,0.06); }
    .psd__warning--high { border-left-color: #f38ba8; background: rgba(243,139,168,0.10); }
    .psd__warning--clickable { cursor: pointer; }
    .psd__warning--clickable:hover { background: rgba(255,255,255,0.06); }
    .psd__warning header {
      display: flex;
      gap: 8px;
      align-items: center;
      margin-bottom: 2px;
    }
    .psd__warning p { margin: 0; color: #cdd6f4; font-size: 0.84rem; line-height: 1.45; }
    .psd__warning footer { display: flex; gap: 6px; margin-top: 4px; flex-wrap: wrap; align-items: baseline; }
    .psd__evidence-label { color: rgba(255,255,255,0.50); font-size: 0.72rem; }
    .psd__evidence {
      background: rgba(0,0,0,0.30);
      padding: 1px 5px;
      border-radius: 3px;
      font-size: 0.74rem;
      color: #c4b5fd;
    }
    .psd__sev {
      text-transform: uppercase;
      font-weight: 600;
      font-size: 0.70rem;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
      color: #cdd6f4;
    }
    .psd__sev--info { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .psd__sev--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .psd__sev--high { background: rgba(243,139,168,0.22); color: #f38ba8; }
    .psd__warn-kind { color: rgba(255,255,255,0.65); font-size: 0.74rem; letter-spacing: 0.02em; }

    .psd__actions { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 6px; }
    .psd__btn {
      background: rgba(137,180,250,0.10);
      color: #cdd6f4;
      border: 1px solid rgba(137,180,250,0.35);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.82rem;
      cursor: pointer;
    }
    .psd__btn:hover:not([disabled]) { background: rgba(137,180,250,0.18); }
    .psd__btn[disabled] { opacity: 0.55; cursor: progress; }
    .psd__btn--ghost { background: rgba(255,255,255,0.06); border-color: rgba(255,255,255,0.12); }
    .psd__btn-spin { margin-left: 4px; opacity: 0.8; }
    .psd__action-msg { margin: 6px 0 0; color: #94e2d5; font-size: 0.78rem; }
    .psd__action-error { margin: 6px 0 0; color: #fda4af; font-size: 0.78rem; }
  `]
})
export class ProjectSteeringDocsSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly svc = inject(SteeringDocsService);
  private readonly jobs = inject(JobService);

  readonly actions = ACTIONS;

  readonly overview = signal<SteeringDocsOverview | null>(null);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);

  readonly openedId = signal<string | null>(null);
  readonly openedRel = signal<string | null>(null);
  readonly fileContent = signal<{ relPath: string; content: string } | null>(null);
  readonly childContent = signal<{ relPath: string; content: string } | null>(null);
  readonly fileLoading = signal<boolean>(false);
  readonly fileError = signal<string | null>(null);

  readonly busyAction = signal<string | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  readonly presentCount = computed(() => this.overview()?.sources.filter(s => s.exists).length ?? 0);
  readonly warningCount = computed(() => this.overview()?.warnings.length ?? 0);

  readonly statusBuckets = computed<StatusBucket[]>(() => {
    const ov = this.overview();
    if (!ov) return [];
    const buckets: StatusBucket[] = [];
    const high = ov.warnings.filter(w => w.severity === 'high').length;
    const warn = ov.warnings.filter(w => w.severity === 'warn').length;
    const info = ov.warnings.filter(w => w.severity === 'info').length;
    const missing = ov.sources.filter(s => !s.exists).length;
    if (high > 0) buckets.push({ label: 'High', count: high, cls: 'missing' });
    if (warn > 0) buckets.push({ label: 'Warn', count: warn, cls: 'missing' });
    if (info > 0) buckets.push({ label: 'Info', count: info, cls: 'present' });
    if (missing > 0) buckets.push({ label: 'Missing files', count: missing, cls: 'missing' });
    return buckets;
  });

  private timer?: ReturnType<typeof setInterval>;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) {
        // Reset cached drilldown state when the project changes.
        this.openedId.set(null);
        this.openedRel.set(null);
        this.fileContent.set(null);
        this.childContent.set(null);
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  refresh(silent = false): void {
    const project = this.projectName();
    if (!project) return;
    if (!silent) this.loading.set(true);
    this.svc.getOverview(project).subscribe({
      next: (ov) => {
        this.overview.set(ov);
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err) => {
        this.error.set(this.describe(err, 'Steering docs API call failed.'));
        this.loading.set(false);
      },
    });
  }

  toggle(s: SteeringDocsSource): void {
    if (this.openedId() === s.id) {
      this.openedId.set(null);
      this.fileContent.set(null);
      this.childContent.set(null);
      this.openedRel.set(null);
      return;
    }
    this.openedId.set(s.id);
    this.fileContent.set(null);
    this.childContent.set(null);
    this.openedRel.set(null);
    this.fileError.set(null);
    if (!s.exists) return;
    if (s.children && s.children.length > 0) {
      // Directory: wait for the user to pick a child file.
      return;
    }
    this.loadFile(s.relPath);
  }

  openChild(relPath: string): void {
    if (this.openedRel() === relPath) {
      this.openedRel.set(null);
      this.childContent.set(null);
      return;
    }
    this.openedRel.set(relPath);
    this.childContent.set(null);
    this.fileError.set(null);
    this.loadChildFile(relPath);
  }

  private loadFile(relPath: string): void {
    const project = this.projectName();
    if (!project) return;
    this.fileLoading.set(true);
    this.svc.getFile(project, relPath).subscribe({
      next: (f) => {
        this.fileContent.set(f);
        this.fileLoading.set(false);
      },
      error: (err) => {
        this.fileError.set(this.describe(err, 'Could not load file.'));
        this.fileLoading.set(false);
      },
    });
  }

  private loadChildFile(relPath: string): void {
    const project = this.projectName();
    if (!project) return;
    this.fileLoading.set(true);
    this.svc.getFile(project, relPath).subscribe({
      next: (f) => {
        this.childContent.set(f);
        this.fileLoading.set(false);
      },
      error: (err) => {
        this.fileError.set(this.describe(err, 'Could not load file.'));
        this.fileLoading.set(false);
      },
    });
  }

  onWarningClick(w: SteeringDocsWarning): void {
    if (!w.sourceId) return;
    const ov = this.overview();
    if (!ov) return;
    const src = ov.sources.find(s => s.id === w.sourceId);
    if (!src) return;
    if (this.openedId() !== src.id) this.toggle(src);
  }

  runAction(action: SteeringAction): void {
    const project = this.projectName();
    if (!project) return;
    this.busyAction.set(action.slug);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyAction.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `steering-${action.slug}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildActionPrompt(action);
        this.jobs.createJob({
          id: slug,
          title: action.label,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: '1-preparation',
        }).subscribe({
          next: (resp) => {
            this.busyAction.set(null);
            this.actionMessage.set(`Queued ${resp?.id ?? slug} in 1-preparation. Promote to 2-ready when scoped.`);
          },
          error: (err) => {
            this.busyAction.set(null);
            this.actionError.set(this.describe(err, 'Could not queue steering task.'));
          },
        });
      },
      error: () => {
        this.busyAction.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  private resolveWatchPath(project: string): Observable<string | null> {
    return this.jobs.getWatchPaths().pipe(map(entries => {
      const match = entries.find(e => e.name === project)
        ?? entries.find(e => e.path === project);
      return match?.path ?? null;
    }));
  }

  private buildActionPrompt(action: SteeringAction): string {
    const ov = this.overview();
    const sources = ov?.sources.map(s => `- \`${s.relPath}\`${s.exists ? '' : ' (missing)'} - ${s.label}`).join('\n') ?? '';
    const warnings = (ov?.warnings ?? [])
      .map(w => `- [${w.severity}] ${w.message}`)
      .join('\n');
    return `# ${action.label}

Spawned from the project Steering Docs surface.

## What to do

${action.description}

Produce a Markdown report; attach it to this task's \`status.md\` (and to the
Analysis Reports archive when an entry is appropriate). Do **not** silently
edit the steering documents; propose changes for review instead.

## Steering inventory at queue time

${sources || '_(no sources known)_'}

## Heuristic warnings at queue time

${warnings || '_(no warnings at queue time)_'}
`;
  }

  // ----------------------------------------------------------------------
  // Display helpers
  // ----------------------------------------------------------------------

  renderMarkdown(content: string): string {
    try { return markdownToHtml(content); } catch { return content; }
  }

  formatTime(iso: string | null | undefined): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  formatSize(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  humanWarningKind(k: string): string {
    switch (k) {
      case 'missingSource': return 'missing source';
      case 'stale': return 'stale';
      case 'possibleConflict': return 'possible conflict';
      case 'recurringFailure': return 'recurring job failure';
      default: return k;
    }
  }

  private describe(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}
