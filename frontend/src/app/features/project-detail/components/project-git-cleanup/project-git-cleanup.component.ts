import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectGitService } from '../../../../services/project-git.service';
import type {
  CleanupCandidate,
  CleanupExecutionItem,
  CleanupTargetKind,
  GitCleanupPlan,
  GitCleanupResult,
} from '../../../git';

type LoadState = 'idle' | 'loading' | 'loaded' | 'error';

/** A display group of cleanup candidates sharing one kind. */
interface CleanupGroup {
  kind: CleanupTargetKind;
  label: string;
  candidates: CleanupCandidate[];
  eligible: number;
}

const KIND_LABELS: Record<CleanupTargetKind, string> = {
  localBranch: 'Local task branches',
  remoteBranch: 'Remote task branches',
  backupRef: 'Backup refs',
  staleWorktree: 'Stale worktrees',
};

const KIND_ORDER: CleanupTargetKind[] = ['localBranch', 'remoteBranch', 'backupRef', 'staleWorktree'];

/**
 * Git-Management cleanup panel (AGT-2009). A project-scoped, two-step destructive
 * flow embedded in the Project Hub Git View:
 *
 *  1. **Analyse** loads a read-only dry-run plan - every `task/*` branch (local +
 *     remote), every `refs/backups/*` ref and every stale worktree, each
 *     classified merged / unmerged / stale against the integration branch. Only
 *     eligible (provably merged, or stale) rows are pre-selected and can be
 *     ticked; ineligible rows show WHY they are kept and cannot be selected.
 *  2. **Delete selected** requires an explicit confirm, then posts the confirmed
 *     subset. The backend re-verifies merge ancestry before every delete
 *     (AGT-1945), so nothing unmerged is ever removed; the result reports how
 *     many refs were dropped and how many were kept.
 *
 * Deliberately never auto-runs: the operator opens it, analyses, reviews the
 * preview, and confirms.
 */
@Component({
  selector: 'app-project-git-cleanup',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-cleanup.component.html',
  styleUrl: './project-git-cleanup.component.scss',
})
export class ProjectGitCleanupComponent {
  private readonly projectGit = inject(ProjectGitService);

  readonly projectName = input.required<string>();

  readonly open = signal(false);

  readonly plan = signal<GitCleanupPlan | null>(null);
  readonly planState = signal<LoadState>('idle');
  readonly planError = signal<string | null>(null);

  /** Keys of the candidates the operator has ticked for deletion. */
  private readonly selected = signal<Set<string>>(new Set<string>());
  readonly confirming = signal(false);

  readonly executing = signal(false);
  readonly result = signal<GitCleanupResult | null>(null);
  readonly resultError = signal<string | null>(null);

  /** Candidates grouped by kind, in a stable display order. */
  readonly groups = computed<CleanupGroup[]>(() => {
    const plan = this.plan();
    if (!plan) return [];
    return KIND_ORDER
      .map(kind => {
        const candidates = plan.candidates.filter(c => c.kind === kind);
        return {
          kind,
          label: KIND_LABELS[kind],
          candidates,
          eligible: candidates.filter(c => c.eligible).length,
        };
      })
      .filter(g => g.candidates.length > 0);
  });

  readonly eligibleCount = computed<number>(() => (this.plan()?.candidates ?? []).filter(c => c.eligible).length);
  readonly selectedCount = computed<number>(() => this.selected().size);
  readonly hasCandidates = computed<boolean>(() => (this.plan()?.candidates.length ?? 0) > 0);

  constructor() {
    // Reset everything whenever the bound project changes.
    effect(() => {
      this.projectName();
      this.open.set(false);
      this.resetPlan();
    });
  }

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next && this.planState() === 'idle') this.loadPlan();
  }

  refresh(): void {
    this.loadPlan();
  }

  candidateKey(c: CleanupCandidate): string {
    return `${c.kind}|${c.remote ?? ''}|${c.name}`;
  }

  isSelected(c: CleanupCandidate): boolean {
    return this.selected().has(this.candidateKey(c));
  }

  toggleCandidate(c: CleanupCandidate): void {
    if (!c.eligible) return;
    const key = this.candidateKey(c);
    const next = new Set(this.selected());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.selected.set(next);
    // Editing the selection invalidates a pending confirm / stale result.
    this.confirming.set(false);
  }

  selectAllEligible(): void {
    const plan = this.plan();
    if (!plan) return;
    this.selected.set(new Set(plan.candidates.filter(c => c.eligible).map(c => this.candidateKey(c))));
    this.confirming.set(false);
  }

  clearSelection(): void {
    this.selected.set(new Set<string>());
    this.confirming.set(false);
  }

  requestDelete(): void {
    if (this.selectedCount() === 0) return;
    this.confirming.set(true);
  }

  cancelDelete(): void {
    this.confirming.set(false);
  }

  confirmDelete(): void {
    const plan = this.plan();
    if (!plan || this.selectedCount() === 0) return;
    const keys = this.selected();
    const items: CleanupExecutionItem[] = plan.candidates
      .filter(c => c.eligible && keys.has(this.candidateKey(c)))
      .map(c => ({ kind: c.kind, name: c.name, remote: c.remote }));
    if (items.length === 0) return;

    this.confirming.set(false);
    this.executing.set(true);
    this.result.set(null);
    this.resultError.set(null);
    this.projectGit.executeCleanup(this.projectName(), items).subscribe({
      next: res => {
        this.result.set(res);
        this.executing.set(false);
        // Re-analyse so the plan reflects what is left after the deletions.
        this.loadPlan();
      },
      error: err => {
        this.resultError.set(this.describeError(err, 'Cleanup failed.'));
        this.executing.set(false);
      },
    });
  }

  trackGroup(_index: number, group: CleanupGroup): string {
    return group.kind;
  }

  trackCandidate(_index: number, c: CleanupCandidate): string {
    return this.candidateKey(c);
  }

  trackAction(index: number): number {
    return index;
  }

  private loadPlan(): void {
    this.planState.set('loading');
    this.planError.set(null);
    this.projectGit.getCleanupPlan(this.projectName()).subscribe({
      next: plan => {
        this.plan.set(plan);
        this.planState.set('loaded');
        if (!plan.isRepo) this.planError.set(plan.error ?? 'This project has no git repository.');
        // Pre-select every eligible candidate so the common "delete all merged"
        // path is one confirm away, while ineligible rows stay untouchable.
        this.selected.set(new Set(plan.candidates.filter(c => c.eligible).map(c => this.candidateKey(c))));
        this.confirming.set(false);
      },
      error: err => {
        this.plan.set(null);
        this.planError.set(this.describeError(err, 'Could not load cleanup plan.'));
        this.planState.set('error');
      },
    });
  }

  private resetPlan(): void {
    this.plan.set(null);
    this.planState.set('idle');
    this.planError.set(null);
    this.selected.set(new Set<string>());
    this.confirming.set(false);
    this.executing.set(false);
    this.result.set(null);
    this.resultError.set(null);
  }

  private describeError(err: unknown, fallback: string): string {
    const record = err as { error?: unknown; message?: string } | null;
    const body = record?.error;
    if (body && typeof body === 'object' && 'error' in body) {
      const message = (body as { error?: unknown }).error;
      if (typeof message === 'string' && message.trim()) return message;
    }
    if (typeof body === 'string' && body.trim()) return body;
    return record?.message || fallback;
  }
}
