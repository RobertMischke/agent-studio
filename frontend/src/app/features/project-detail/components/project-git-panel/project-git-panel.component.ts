import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { DiffContentComponent } from '../../../../components/diff-content/diff-content.component';
import { ProjectGitCleanupComponent } from '../project-git-cleanup/project-git-cleanup.component';
import { ProjectGitHistoryComponent } from '../project-git-history/project-git-history.component';
import { ProjectGitTreeComponent } from '../project-git-tree/project-git-tree.component';
import { ProjectGitService } from '../../../../services/project-git.service';
import { formatCompactDateTime } from '../../../../services/format.util';
import { describeDiffSize, isLargeDiff } from '../../../../utils/large-diff-gate';
import {
  buildGitTree,
  type GitActiveCheckout,
  type GitBranchEntry,
  type GitFileChange,
  type GitGraphCommit,
  type GitProjectInventory,
  type GitWorktreeEntry,
} from '../../../git';

type LoadState = 'idle' | 'loading' | 'loaded' | 'error';

/** What the right pane is describing + which SHA drives its file list. */
type GitSelection =
  | { kind: 'branch'; branch: GitBranchEntry }
  | { kind: 'worktree'; worktree: GitWorktreeEntry }
  | { kind: 'active'; checkout: GitActiveCheckout }
  | { kind: 'commit'; commit: GitGraphCommit };

/**
 * Project Hub "Git View" panel. A project-scoped, read-only branch / worktree /
 * history browser: the left pane is a grouped tree (worktrees, integration /
 * feature / task branches, recent history) built by the pure `buildGitTree`
 * model; the right pane shows the selected node's detail and, for anything with
 * a commit SHA, its changed files and per-file diff. The diff is rendered by the
 * shared {@link DiffContentComponent}, so no second diff renderer is introduced.
 *
 * Deliberately project-scoped (not a global git client) and non-destructive:
 * every backend call is a read.
 */
@Component({
  selector: 'app-project-git-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DiffContentComponent,
    ProjectGitCleanupComponent,
    ProjectGitHistoryComponent,
    ProjectGitTreeComponent,
  ],
  templateUrl: './project-git-panel.component.html',
  styleUrl: './project-git-panel.component.scss',
})
export class ProjectGitPanelComponent {
  private readonly projectGit = inject(ProjectGitService);

  readonly projectName = input.required<string>();

  readonly inventory = signal<GitProjectInventory | null>(null);
  readonly inventoryState = signal<LoadState>('idle');
  readonly inventoryError = signal<string | null>(null);
  readonly commits = signal<GitGraphCommit[]>([]);
  readonly historyHasMore = signal(false);
  readonly historyNextOffset = signal<number | null>(null);
  readonly historyLoading = signal(false);
  readonly historyError = signal<string | null>(null);

  readonly selection = signal<GitSelection | null>(null);
  readonly changesOpen = signal(false);

  readonly files = signal<GitFileChange[]>([]);
  readonly filesState = signal<LoadState>('idle');
  readonly filesError = signal<string | null>(null);
  readonly selectedPath = signal<string | null>(null);
  readonly diffText = signal('');
  readonly diffState = signal<LoadState>('idle');
  readonly diffError = signal<string | null>(null);
  readonly diffEmptyMessage = signal<string | null>(null);

  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  readonly revealAllLargeDiffs = signal(false);

  private filesLoadKey = '';
  private diffLoadKey = '';

  /** Grouped tree for the left pane; empty when the project is not a repo. */
  readonly tree = computed(() => buildGitTree(this.inventory()));

  /** True when the panel should show the empty/error card instead of panes. */
  readonly showEmpty = computed<boolean>(() => {
    if (this.inventoryState() === 'error') return true;
    const inv = this.inventory();
    return !!inv && !inv.isRepo;
  });

  /** Node id of the current selection, for the tree row highlight. */
  readonly selectedId = computed<string | null>(() => {
    const sel = this.selection();
    if (!sel) return null;
    if (sel.kind === 'branch') return `branch:${sel.branch.name}`;
    if (sel.kind === 'worktree') return `wt:${sel.worktree.path}`;
    if (sel.kind === 'active') return `active:${sel.checkout.task.taskKey}`;
    return null;
  });

  /** The SHA whose files + diff the right pane loads for the current selection. */
  readonly activeSha = computed<string | null>(() => {
    const sel = this.selection();
    if (!sel) return null;
    if (sel.kind === 'branch') return sel.branch.tipSha;
    if (sel.kind === 'worktree') return sel.worktree.headSha;
    if (sel.kind === 'active') return sel.checkout.headSha;
    return sel.commit.sha;
  });

  readonly selectedCommitSha = computed<string | null>(() => this.activeSha());

  readonly selectedFile = computed<GitFileChange | null>(() => {
    const path = this.selectedPath();
    if (!path) return null;
    return this.files().find(f => f.path === path) ?? null;
  });

  readonly diffIsLarge = computed<boolean>(() => isLargeDiff(this.diffText()));
  readonly diffSizeLabel = computed<string>(() => describeDiffSize(this.diffText()));
  readonly diffGated = computed<boolean>(() => {
    if (!this.diffIsLarge()) return false;
    if (this.revealAllLargeDiffs()) return false;
    const path = this.selectedPath();
    return !(path && this.revealedPaths().has(path));
  });

  constructor() {
    // Re-fetch whenever the bound project changes; clear any stale selection.
    effect(() => {
      const name = this.projectName();
      this.selection.set(null);
      this.resetFiles();
      this.loadInventory(name);
    });
  }

  refresh(): void {
    this.loadInventory(this.projectName());
  }

  private loadInventory(project: string): void {
    this.inventoryState.set('loading');
    this.inventoryError.set(null);
    this.projectGit.getInventory(project).subscribe({
      next: inv => {
        this.inventory.set(inv);
        this.commits.set(inv.history?.commits ?? []);
        this.historyHasMore.set(inv.history?.hasMore ?? false);
        this.historyNextOffset.set(inv.history?.nextOffset ?? null);
        this.historyError.set(null);
        this.inventoryState.set('loaded');
        if (inv && !inv.isRepo) this.inventoryError.set(inv.error ?? 'This project has no git repository.');
      },
      error: err => {
        this.inventory.set(null);
        this.inventoryError.set(this.describeError(err, 'Could not load git inventory.'));
        this.inventoryState.set('error');
      },
    });
  }

  selectBranch(branch: GitBranchEntry): void {
    this.selection.set({ kind: 'branch', branch });
    this.closeChanges();
  }

  selectWorktree(worktree: GitWorktreeEntry): void {
    this.selection.set({ kind: 'worktree', worktree });
    this.closeChanges();
  }

  selectActive(checkout: GitActiveCheckout): void {
    this.selection.set({ kind: 'active', checkout });
    this.closeChanges();
  }

  selectCommit(commit: GitGraphCommit): void {
    this.selection.set({ kind: 'commit', commit });
    this.closeChanges();
  }

  inspectChanges(commit: GitGraphCommit): void {
    this.selection.set({ kind: 'commit', commit });
    this.changesOpen.set(true);
    const key = `${this.projectName()}|${commit.sha}`;
    if (key === this.filesLoadKey && this.filesState() === 'loaded') return;
    this.filesLoadKey = key;
    this.loadFiles(this.projectName(), commit.sha, key);
  }

  loadOlder(): void {
    const offset = this.historyNextOffset();
    if (offset === null || this.historyLoading()) return;
    this.historyLoading.set(true);
    this.historyError.set(null);
    this.projectGit.getHistory(this.projectName(), offset).subscribe({
      next: page => {
        const known = new Set(this.commits().map(commit => commit.sha));
        this.commits.update(current => [
          ...current,
          ...page.commits.filter(commit => !known.has(commit.sha)),
        ]);
        this.historyHasMore.set(page.hasMore);
        this.historyNextOffset.set(page.nextOffset);
        this.historyLoading.set(false);
      },
      error: err => {
        this.historyError.set(this.describeError(err, 'Could not load older commits.'));
        this.historyLoading.set(false);
      },
    });
  }

  selectFile(path: string): void {
    if (this.selectedPath() === path) return;
    this.selectedPath.set(path);
    const sha = this.activeSha();
    if (sha) this.loadDiff(this.projectName(), sha, path);
  }

  revealCurrentDiff(): void {
    const path = this.selectedPath();
    if (!path) return;
    const next = new Set(this.revealedPaths());
    next.add(path);
    this.revealedPaths.set(next);
  }

  revealAll(): void {
    this.revealAllLargeDiffs.set(true);
  }

  retryDiff(): void {
    const sha = this.activeSha();
    const path = this.selectedPath();
    if (sha && path) this.loadDiff(this.projectName(), sha, path);
  }

  private loadFiles(project: string, sha: string, requestKey: string): void {
    this.filesState.set('loading');
    this.filesError.set(null);
    this.resetDiff();
    this.projectGit.getCommitFiles(project, sha).subscribe({
      next: resp => {
        if (requestKey !== this.filesLoadKey) return;
        const files = resp.files ?? [];
        this.files.set(files);
        this.filesState.set('loaded');
        const first = files[0]?.path ?? null;
        this.selectedPath.set(first);
        if (first) this.loadDiff(project, sha, first);
      },
      error: err => {
        if (requestKey !== this.filesLoadKey) return;
        this.files.set([]);
        this.selectedPath.set(null);
        this.filesError.set(this.describeError(err, 'Could not load commit files.'));
        this.filesState.set('error');
      },
    });
  }

  private loadDiff(project: string, sha: string, path: string): void {
    const requestKey = `${project}|${sha}|${path}`;
    this.diffLoadKey = requestKey;
    this.diffState.set('loading');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
    this.diffText.set('');
    this.projectGit.getCommitDiff(project, sha, path).subscribe({
      next: resp => {
        if (requestKey !== this.diffLoadKey) return;
        const diff = typeof resp?.diff === 'string' ? resp.diff : '';
        this.diffText.set(diff);
        this.diffEmptyMessage.set(
          diff.trim().length > 0 ? null : resp?.emptyReason ?? 'No diff for this path in the selected commit.',
        );
        this.diffState.set('loaded');
      },
      error: err => {
        if (requestKey !== this.diffLoadKey) return;
        this.diffError.set(this.describeError(err, 'Could not load diff.'));
        this.diffState.set('error');
      },
    });
  }

  // ----- display helpers -----

  when(iso: string | null | undefined): string {
    return iso ? formatCompactDateTime(iso) : '';
  }

  aheadBehind(branch: GitBranchEntry): string {
    const parts: string[] = [];
    if (branch.ahead > 0) parts.push(`↑${branch.ahead}`);
    if (branch.behind > 0) parts.push(`↓${branch.behind}`);
    return parts.join(' ');
  }

  short(sha: string | null | undefined): string {
    if (!sha) return '';
    return sha.length > 7 ? sha.slice(0, 7) : sha;
  }

  trackFile(_index: number, file: GitFileChange): string {
    return file.path;
  }

  closeChanges(): void {
    this.changesOpen.set(false);
    this.filesLoadKey = '';
    this.resetFiles();
  }

  private resetFiles(): void {
    this.files.set([]);
    this.filesState.set('idle');
    this.filesError.set(null);
    this.selectedPath.set(null);
    this.revealedPaths.set(new Set<string>());
    this.revealAllLargeDiffs.set(false);
    this.resetDiff();
  }

  private resetDiff(): void {
    this.diffLoadKey = '';
    this.diffText.set('');
    this.diffState.set('idle');
    this.diffError.set(null);
    this.diffEmptyMessage.set(null);
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
