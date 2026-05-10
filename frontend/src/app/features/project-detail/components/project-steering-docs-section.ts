import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { SteeringDocsService } from '../../../services/steering-docs.service';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import {
  SteeringDocsOverview,
  SteeringDocsSource,
  SteeringDocsWarning,
} from '../../../models/steering-docs.model';
import { markdownToHtml } from '../../../components/markdown-utils';

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
  templateUrl: './project-steering-docs-section.html',
  styleUrl: './project-steering-docs-section.scss'
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

  private timer?: VisibleIntervalHandle;

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
    this.timer = setVisibleInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
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
