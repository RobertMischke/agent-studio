import { Injectable, OnDestroy, computed, signal, inject } from '@angular/core';
import type { JobInfo } from '../../../models/task.model';
import type {
  GitFileChange,
  GitStatus,
  JobCommitDetail,
  JobCommitInfo,
  JobExcludedCommitInfo,
  RecentCommit,
} from '../../../features/git';
import { JobService } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import {
  setVisibleInterval,
  clearVisibleInterval,
  VisibleIntervalHandle,
} from '../../../utils/visible-interval';

/**
 * Owns the Git pane state and API calls for a single job-detail
 * instance: working-tree status, the currently-selected diff path and
 * its body, the commit-message draft, and the generate/commit progress
 * flags.
 *
 * Provided locally on JobDetailComponent. The component supplies the
 * current `JobInfo` (via setJob) and the service drives all backend
 * traffic + signals from there.
 */
@Injectable()
export class GitPaneService implements OnDestroy {
  private jobService = inject(JobService);
  private errorDialog = inject(ErrorDialogService);

  readonly status = signal<GitStatus | null>(null);
  readonly loading = signal(false);
  readonly selectedDiffPath = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly commitMessage = signal('');
  readonly committing = signal(false);
  readonly generatingMsg = signal(false);

  /**
   * Small LRU diff cache keyed by `(mode|sha|path)`. Selecting a file in
   * the tree fires a backend `git diff` / `git show` round-trip; without
   * a cache, clicking back-and-forth across files repeatedly pays the
   * network cost and the diff2html re-render. The cache invalidates
   * whenever `setJob` is called for a different job, when the working
   * tree refreshes (`refresh()` resets the worktree slice), and on
   * commit (history changes). 32 entries keeps memory bounded for the
   * largest realistic change sets.
   */
  private readonly DIFF_CACHE_LIMIT = 32;
  private diffCache = new Map<string, string>();
  private cacheKey(path: string): string {
    if (this.viewMode() === 'commit') {
      const sha = this.selectedCommitSha() ?? '';
      return `commit|${sha}|${path}`;
    }
    return `worktree|${path}`;
  }
  private cacheGet(key: string): string | undefined {
    const v = this.diffCache.get(key);
    if (v !== undefined) {
      // Touch LRU order.
      this.diffCache.delete(key);
      this.diffCache.set(key, v);
    }
    return v;
  }
  private cachePut(key: string, value: string): void {
    if (this.diffCache.has(key)) this.diffCache.delete(key);
    this.diffCache.set(key, value);
    while (this.diffCache.size > this.DIFF_CACHE_LIMIT) {
      const oldest = this.diffCache.keys().next().value;
      if (oldest === undefined) break;
      this.diffCache.delete(oldest);
    }
  }
  private clearDiffCache(): void { this.diffCache.clear(); }
  private invalidateWorktreeCache(): void {
    for (const key of [...this.diffCache.keys()]) {
      if (key.startsWith('worktree|')) this.diffCache.delete(key);
    }
  }

  // Commit-history view: when the task has an auto-commit recorded, the
  // pane switches from "live working tree" to "what this task changed".
  // That data survives future work in the repo and is what the user wants
  // to see when reviewing a finished task.
  readonly commitDetail = signal<JobCommitDetail | null>(null);
  readonly viewMode = computed<'commit' | 'worktree'>(() =>
    this.commitDetail()?.commit ? 'commit' : 'worktree',
  );

  /**
   * Ordered chain of commits attributed to this task (oldest -&gt; newest).
   * Mirrors <c>JobInfo.commits</c>; surfaces an in-memory list so the
   * git-pane can render a multi-commit strip and let the user pick which
   * commit's detail to display.
   */
  readonly commitChain = signal<JobCommitInfo[]>([]);
  /** SHA of the commit currently rendered in the commit detail view. Defaults to the newest entry. */
  readonly selectedCommitSha = signal<string | null>(null);

  /**
   * Commits the attribution rule withheld from this task (ADR
   * "Commit-Attribution-Regel"). Mirrors <c>JobInfo.excludedCommits</c>;
   * surfaced under the git-pane "(N excluded)" expander so the operator can
   * see why each was held back and restore it if the rule got it wrong.
   */
  readonly excludedCommits = signal<JobExcludedCommitInfo[]>([]);
  /** True while an exclude/include override round-trip is in flight. */
  readonly overrideBusy = signal(false);

  /**
   * "+ Add commit" picker state: recent branch commits the operator can
   * attach to this task plus the open/loading flags driving the dropdown.
   * Commits already on the chain or excluded list are filtered out by
   * {@link addableCommits} so the picker only offers genuinely new ones.
   */
  readonly addPickerOpen = signal(false);
  readonly recentCommits = signal<RecentCommit[]>([]);
  readonly recentLoading = signal(false);
  readonly addableCommits = computed<RecentCommit[]>(() => {
    const taken = new Set<string>([
      ...this.commitChain().map((c) => c.sha),
      ...this.excludedCommits().map((c) => c.sha),
    ]);
    return this.recentCommits().filter((c) => !taken.has(c.sha));
  });

  private currentJob: JobInfo | null = null;
  private refreshTimer: VisibleIntervalHandle | null = null;

  /** Start polling git status every `intervalMs` ms. No-op if already running. */
  startAutoRefresh(intervalMs = 5000): void {
    if (this.refreshTimer) return;
    this.refreshTimer = setVisibleInterval(() => {
      // In commit mode the displayed snapshot is historical — polling the
      // working tree would just churn for nothing.
      if (this.viewMode() === 'commit') return;
      if (!this.committing() && !this.generatingMsg()) {
        this.refresh();
      }
    }, intervalMs);
  }

  /** Stop the auto-refresh polling loop. */
  stopAutoRefresh(): void {
    if (this.refreshTimer) {
      clearVisibleInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.stopAutoRefresh();
  }

  /**
   * Tell the service which job is currently displayed. Resets the pane
   * state when the job actually changes; same-job calls are no-ops so
   * we don't blow away in-flight selections.
   */
  setJob(info: JobInfo | null | undefined): void {
    const sameJob =
      this.currentJob &&
      info &&
      this.currentJob.id === info.id &&
      this.currentJob.watchPath === info.watchPath;
    if (sameJob) {
      const hadCommit = !!this.currentJob!.commit;
      const oldChainLen = this.commitChain().length;
      this.currentJob = info!;
      this.commitChain.set(info!.commits ?? (info!.commit ? [info!.commit] : []));
      this.excludedCommits.set(info!.excludedCommits ?? []);
      // The auto-commit lands on the progress→review transition, so a
      // refresh of the same job can flip from "no commit" to "has commit".
      // Load the snapshot lazily when that happens. Also reload when a
      // new commit lands on the chain (continue-mode follow-up, recovery
      // commit, operator-driven steer).
      const newChainLen = this.commitChain().length;
      if ((!hadCommit && info!.commit) || newChainLen > oldChainLen) {
        const newest = this.commitChain()[newChainLen - 1] ?? null;
        if (newest) {
          this.selectedCommitSha.set(newest.sha);
        }
        this.loadCommitDetail();
      }
      return;
    }
    this.currentJob = info ?? null;
    this.status.set(null);
    this.commitDetail.set(null);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    this.commitMessage.set('');
    this.loading.set(false);
    this.committing.set(false);
    this.generatingMsg.set(false);
    this.clearDiffCache();
    const chain = info?.commits ?? (info?.commit ? [info.commit] : []);
    this.commitChain.set(chain);
    this.excludedCommits.set(info?.excludedCommits ?? []);
    this.selectedCommitSha.set(chain.length > 0 ? chain[chain.length - 1].sha : null);
    if (info?.commit || chain.length > 0) this.loadCommitDetail();
  }

  /**
   * Switch the commit detail view to a specific commit on the task's
   * chain. Validates the SHA against the chain to keep this method's
   * blast radius bounded - it cannot be coaxed into loading arbitrary
   * repository commits even before the backend's IsKnownJobCommit
   * gate refuses unrelated SHAs.
   */
  selectChainCommit(sha: string): void {
    const info = this.currentJob;
    if (!info) return;
    const entry = this.commitChain().find((c) => c.sha === sha);
    if (!entry) return;
    if (this.selectedCommitSha() === sha) return;
    this.selectedCommitSha.set(sha);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    // Compose a JobCommitDetail header from the chain entry directly,
    // then load the file list from the per-sha endpoint. We don't have
    // a single "commit by sha" endpoint that returns both, so we shape
    // the same object here for the template's sake.
    this.commitDetail.set({ commit: entry, files: [] });
    this.jobService.getJobCommitFilesBySha(info.id, sha, info.watchPath).subscribe({
      next: (res) => {
        const files: GitFileChange[] = res?.files ?? [];
        this.commitDetail.set({ commit: entry, files });
        const first = files[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.commitDetail.set({ commit: entry, files: [] }),
    });
  }

  /**
   * Load the recorded-commit snapshot for the current job. Tasks that have
   * been auto-committed on progress→review carry a JobCommitInfo on
   * job.json — the backend re-derives the file list from `git show` so the
   * pane stays accurate even after history rewrites.
   */
  loadCommitDetail(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.getJobCommit(info.id, info.watchPath).subscribe({
      next: (detail) => {
        this.commitDetail.set(detail);
        // Default-select the first changed file so the diff is visible at a
        // glance — matches the user's intent of "show me the changes".
        const first = detail?.files?.[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.commitDetail.set(null),
    });
  }

  refresh(): void {
    const info = this.currentJob;
    if (!info) return;
    this.loading.set(true);
    // Worktree diff slice can shift between refreshes (file edited again,
    // staged, etc.); drop any cached worktree entries so the next click
    // picks up a fresh diff. Commit-mode entries are immutable per sha,
    // so we keep those.
    this.invalidateWorktreeCache();
    this.jobService.getGitStatus(info.id, info.watchPath).subscribe({
      next: (status) => {
        this.status.set(status);
        this.loading.set(false);
        // If a previously selected file is no longer in the change set,
        // clear the diff so we don't keep stale text on screen.
        const selected = this.selectedDiffPath();
        if (selected && !status.files.some((f) => f.path === selected)) {
          this.selectedDiffPath.set(null);
          this.diffText.set('');
        }
      },
      error: (err) => {
        this.loading.set(false);
        this.errorDialog.show(err, { title: 'Git status failed', source: `Task ${info.id}` });
      },
    });
  }

  selectDiffPath(path: string): void {
    if (this.selectedDiffPath() === path) {
      this.selectedDiffPath.set(null);
      this.diffText.set('');
      return;
    }
    const info = this.currentJob;
    if (!info) return;
    this.selectedDiffPath.set(path);
    // Cache lookup: clicking back-and-forth between files in the tree is
    // a common pattern; without the cache each toggle pays the network
    // round-trip and the diff2html re-render. Hit -> set synchronously.
    const cacheKey = this.cacheKey(path);
    const cached = this.cacheGet(cacheKey);
    if (cached !== undefined) {
      this.diffText.set(cached);
      return;
    }
    this.diffText.set('');
    // In commit mode the diff comes from `git show <sha> -- <path>` so we
    // see the historical change, not whatever the working tree looks like
    // right now. When the selected commit is one of the older entries on
    // the chain (not the newest singular commit), route to the per-sha
    // diff endpoint instead so the displayed diff matches the picked
    // commit, not whichever one happens to be on `JobInfo.commit`.
    if (this.viewMode() === 'commit') {
      const selectedSha = this.selectedCommitSha();
      const newest = this.commitChain()[this.commitChain().length - 1]?.sha ?? null;
      const useShaEndpoint = selectedSha != null && selectedSha !== newest;
      const setDiff = (res: unknown) => {
        let text = '';
        if (typeof res === 'string') text = res;
        else if (res && typeof res === 'object' && 'diff' in (res as Record<string, unknown>)) {
          const d = (res as { diff?: string }).diff;
          text = typeof d === 'string' ? d : '';
        }
        this.diffText.set(text);
        if (text) this.cachePut(cacheKey, text);
      };
      const handlers = {
        next: setDiff,
        error: () => this.diffText.set('(failed to load diff)'),
      };
      if (useShaEndpoint) {
        this.jobService
          .getJobCommitDiffBySha(info.id, selectedSha!, path, info.watchPath)
          .subscribe(handlers);
      } else {
        this.jobService.getJobCommitDiff(info.id, path, info.watchPath).subscribe(handlers);
      }
      return;
    }
    this.jobService.getGitDiff(info.id, path, info.watchPath).subscribe({
      next: (text: unknown) => {
        const t = typeof text === 'string' ? text : '';
        this.diffText.set(t);
        if (t) this.cachePut(cacheKey, t);
      },
      error: () => this.diffText.set('(failed to load diff)'),
    });
  }

  generateCommitMessage(): void {
    const info = this.currentJob;
    if (!info) return;
    this.generatingMsg.set(true);
    this.jobService.generateCommitMessage(info.id, info.watchPath).subscribe({
      next: (res) => {
        this.generatingMsg.set(false);
        if (res?.message) this.commitMessage.set(res.message);
      },
      error: (err) => {
        this.generatingMsg.set(false);
        this.errorDialog.show(err, {
          title: 'Generate commit message failed',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  commit(): void {
    const info = this.currentJob;
    const msg = this.commitMessage().trim();
    if (!info || !msg) return;
    this.committing.set(true);
    this.jobService.commitJob(info.id, msg, info.watchPath).subscribe({
      next: () => {
        this.committing.set(false);
        this.commitMessage.set('');
        this.refresh();
      },
      error: (err) => {
        this.committing.set(false);
        this.errorDialog.show(err, { title: 'Commit failed', source: `Task ${info.id}` });
      },
    });
  }

  /**
   * Operator override: withhold a commit the rule engine attributed to this
   * task. Optimistically moves it from the chain into the excluded list so
   * the pane updates immediately; the backend Updated() push later reconciles
   * the persisted truth. ADR "Commit-Attribution-Regel".
   */
  excludeCommit(sha: string): void {
    const info = this.currentJob;
    if (!info || this.overrideBusy()) return;
    const entry = this.commitChain().find((c) => c.sha === sha);
    if (!entry) return;
    this.overrideBusy.set(true);
    this.jobService.excludeCommit(info.id, sha, info.watchPath).subscribe({
      next: () => {
        this.overrideBusy.set(false);
        this.commitChain.update((chain) => chain.filter((c) => c.sha !== sha));
        this.excludedCommits.update((ex) => [
          ...ex,
          {
            sha: entry.sha,
            shortSha: entry.shortSha,
            reason: 'manual-exclude',
            subject: (entry.message ?? '').split('\n')[0],
            at: entry.at,
            manual: true,
          },
        ]);
        if (this.selectedCommitSha() === sha) {
          const next = this.commitChain();
          const newest = next.length > 0 ? next[next.length - 1].sha : null;
          this.selectedCommitSha.set(newest);
          if (newest) this.selectChainCommit(newest);
          else this.commitDetail.set(null);
        }
      },
      error: (err) => {
        this.overrideBusy.set(false);
        this.errorDialog.show(err, { title: 'Exclude commit failed', source: `Task ${info.id}` });
      },
    });
  }

  /**
   * Operator override: restore an excluded commit back into this task's set.
   * Optimistically moves it from the excluded list onto the chain; the
   * backend push reconciles attribution kind + ordering.
   */
  includeCommit(sha: string): void {
    const info = this.currentJob;
    if (!info || this.overrideBusy()) return;
    const ex = this.excludedCommits().find((c) => c.sha === sha);
    if (!ex) return;
    this.overrideBusy.set(true);
    this.jobService
      .includeCommit(info.id, sha, { message: ex.subject, at: ex.at }, info.watchPath)
      .subscribe({
        next: () => {
          this.overrideBusy.set(false);
          this.excludedCommits.update((list) => list.filter((c) => c.sha !== sha));
          this.commitChain.update((chain) => [
            ...chain,
            {
              sha: ex.sha,
              shortSha: ex.shortSha,
              message: ex.subject ?? '',
              filesChanged: 0,
              files: [],
              at: ex.at ?? new Date().toISOString(),
              attribution: 'manual-include-after-exclude',
            },
          ]);
        },
        error: (err) => {
          this.overrideBusy.set(false);
          this.errorDialog.show(err, { title: 'Include commit failed', source: `Task ${info.id}` });
        },
      });
  }

  /** Toggle the "+ Add commit" picker, lazily loading recent commits on open. */
  toggleAddPicker(): void {
    const next = !this.addPickerOpen();
    this.addPickerOpen.set(next);
    if (next && this.recentCommits().length === 0) this.loadRecentCommits();
  }

  /** Fetch recent branch commits for the picker. */
  loadRecentCommits(): void {
    const info = this.currentJob;
    if (!info) return;
    this.recentLoading.set(true);
    this.jobService.getRecentCommits(info.id, info.watchPath).subscribe({
      next: (res) => {
        this.recentCommits.set(res?.commits ?? []);
        this.recentLoading.set(false);
      },
      error: () => {
        this.recentCommits.set([]);
        this.recentLoading.set(false);
      },
    });
  }

  /**
   * Operator override: attach a recent commit the rule engine never saw
   * ("+ Add commit" -> manual-add). Optimistically appends it to the chain;
   * the backend enriches the stored entry with a real file list + subject.
   */
  addRecentCommit(commit: RecentCommit): void {
    const info = this.currentJob;
    if (!info || this.overrideBusy()) return;
    this.overrideBusy.set(true);
    this.jobService
      .includeCommit(info.id, commit.sha, { message: commit.subject, at: commit.authorDateUtc }, info.watchPath)
      .subscribe({
        next: () => {
          this.overrideBusy.set(false);
          this.addPickerOpen.set(false);
          this.commitChain.update((chain) => [
            ...chain,
            {
              sha: commit.sha,
              shortSha: commit.shortSha,
              message: commit.subject,
              filesChanged: commit.filesChanged,
              files: [],
              at: commit.authorDateUtc,
              attribution: 'manual-add',
            },
          ]);
        },
        error: (err) => {
          this.overrideBusy.set(false);
          this.errorDialog.show(err, { title: 'Add commit failed', source: `Task ${info.id}` });
        },
      });
  }

  openInVsCode(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.openInVsCode(info.id, info.watchPath).subscribe({
      error: (err) =>
        this.errorDialog.show(err, { title: 'Open in VS Code failed', source: `Task ${info.id}` }),
    });
  }
}
