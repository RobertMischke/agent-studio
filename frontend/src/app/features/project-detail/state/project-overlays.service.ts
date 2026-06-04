import { Injectable, computed, signal } from '@angular/core';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectRailKey,
  ProjectRailKey,
  toProjectSlug,
} from '../components/project-shell/project-shell.config';

/**
 * Cycle 9g project-detail feature service: open/close state + URL-hash
 * sync for the four per-project overlays that stack above the kanban
 * shell:
 *
 *   - orchestrator-feed (per-project log)          `#/project/<slug>/feed`
 *   - project-shell     (full project window)      `#/projects/<slug>[/<rail>]`
 *   - analysis-report   (drill-down on one report) no hash
 *
 * Lifted out of `app.ts` per ADR-0034. The `<app-project-overlays>`
 * container injects this service and renders all four overlays. The
 * shell keeps thin pass-through methods for the entry points that
 * remain shell-coordinated. The legacy project-detail overlay is folded
 * into the project-shell `settings` rail.
 *
 * The project-shell hash sync requires the workspace's watch-paths
 * (slug → name resolution); the shell calls `syncShellFromHash` on the
 * `hashchange` event and on `/api/watch-paths` success, passing the
 * current entries.
 */
@Injectable({ providedIn: 'root' })
export class ProjectOverlaysService {
  readonly orchFeedProject = signal<string | null>(null);
  readonly projectShellName = signal<string | null>(null);
  readonly projectShellRail = signal<ProjectRailKey>(DEFAULT_PROJECT_RAIL_KEY);
  readonly analysisReportFocus = signal<{ project: string; reportId: string } | null>(null);

  readonly anyOpen = computed(() =>
    this.orchFeedProject() !== null
    || this.projectShellName() !== null
    || this.analysisReportFocus() !== null);

  private readonly shellHashPrefix = '#/projects/';
  // Singular `#/project/` (no trailing `s`) so the feed anchor cannot be
  // confused with the plural `#/projects/` project-shell prefix above.
  private readonly feedHashPrefix = '#/project/';
  private readonly feedHashSuffix = '/feed';
  /**
   * True when the visible feed was reached via a deep-link hash, so a
   * back/forward navigation that drops the hash closes it. A feed opened
   * by a button (toolbar / hub) or stacked over the project-shell
   * (`openFeedFromShell`) survives unrelated hash churn — without this
   * flag a shell-hash reconciliation would yank the stacked feed shut.
   */
  private openedFeedViaHash = false;

  // ---------- orch-feed (URL-deep-linked) ----------

  /**
   * Open the per-project orchestrator feed and stamp a deep-link hash
   * (`#/project/<slug>/feed`) so a bookmark or reload reproduces the
   * open feed. Mirrors the project-shell anchor contract; pushState does
   * not fire `hashchange`, so callers re-run `syncFeedFromHash` on the
   * next watch-path resolution to keep the slug → name mapping honest.
   */
  openOrchFeed(name: string): void {
    this.orchFeedProject.set(name);
    this.openedFeedViaHash = false;
    const slug = toProjectSlug(name);
    if (!slug) return;
    const target = `${this.feedHashPrefix}${slug}${this.feedHashSuffix}`;
    if (window.location.hash !== target) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
    this.openedFeedViaHash = false;
    if (this.isFeedHash(window.location.hash)) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search);
      } catch { /* ignore */ }
    }
  }

  /**
   * Reconcile the orch-feed signal with the URL hash. Accepts
   * `#/project/<slug>/feed`. Slug → name resolution requires the
   * workspace watch-paths; if they have not loaded yet we leave the
   * signal alone — call again when they do (the shell re-runs this on
   * `/api/watch-paths` success, same as the project-shell sync).
   */
  syncFeedFromHash(watchPaths: readonly { name: string }[]): void {
    const hash = window.location.hash;
    if (!this.isFeedHash(hash)) {
      // Only a hash-opened feed closes when its hash is dropped; a
      // button-opened or shell-stacked feed survives unrelated churn.
      if (this.openedFeedViaHash && this.orchFeedProject() !== null) {
        this.orchFeedProject.set(null);
        this.openedFeedViaHash = false;
      }
      return;
    }
    const slug = decodeURIComponent(
      hash.slice(this.feedHashPrefix.length, hash.length - this.feedHashSuffix.length)
    ).toLowerCase();
    if (!slug) return;
    if (watchPaths.length === 0) return;
    const match = watchPaths.find(wp => toProjectSlug(wp.name) === slug);
    if (!match) {
      if (this.openedFeedViaHash && this.orchFeedProject() !== null) {
        this.orchFeedProject.set(null);
        this.openedFeedViaHash = false;
      }
      return;
    }
    if (this.orchFeedProject() !== match.name) this.orchFeedProject.set(match.name);
    this.openedFeedViaHash = true;
  }

  private isFeedHash(hash: string): boolean {
    return hash.startsWith(this.feedHashPrefix) && hash.endsWith(this.feedHashSuffix)
      && hash.length > this.feedHashPrefix.length + this.feedHashSuffix.length;
  }

  // ---------- project-shell (URL-deep-linked) ----------

  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY,
                   watchPaths: readonly { name: string }[] = []): void {
    const slug = toProjectSlug(name);
    if (!slug) return;
    const target = `${this.shellHashPrefix}${slug}`
      + (rail !== DEFAULT_PROJECT_RAIL_KEY ? `/${rail}` : '');
    if (window.location.hash !== target) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
    // pushState doesn't fire hashchange; apply the resolved state directly.
    this.syncShellFromHash(watchPaths);
  }

  closeProjectShell(): void {
    this.projectShellName.set(null);
    this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
    if (window.location.hash.startsWith(this.shellHashPrefix)) {
      try {
        history.pushState(null, '', window.location.pathname + window.location.search);
      } catch { /* ignore */ }
    }
  }

  setProjectShellRail(key: ProjectRailKey): void {
    const name = this.projectShellName();
    if (!name) return;
    this.projectShellRail.set(key);
    const slug = toProjectSlug(name);
    const target = `${this.shellHashPrefix}${slug}`
      + (key !== DEFAULT_PROJECT_RAIL_KEY ? `/${key}` : '');
    if (window.location.hash !== target) {
      try {
        history.replaceState(null, '', window.location.pathname + window.location.search + target);
      } catch { /* ignore */ }
    }
  }

  /**
   * Cross-overlay nav: clicking "open feed" inside the project-shell
   * stacks the orchestrator feed over the shell. The shell stays
   * mounted so closing the feed returns to the same rail.
   */
  openFeedFromShell(): void {
    const name = this.projectShellName();
    if (!name) return;
    this.orchFeedProject.set(name);
  }

  /**
   * Cross-overlay nav retained for the settings rail, which mounts the
   * former project-detail component inside the central project window.
   */
  openFeedFromDetail(name: string): void {
    this.orchFeedProject.set(name);
  }

  // ---------- analysis-report ----------

  openAnalysisReport(project: string, reportId: string): void {
    this.analysisReportFocus.set({ project, reportId });
  }

  closeAnalysisReport(): void {
    this.analysisReportFocus.set(null);
  }

  /**
   * Reconcile the project-shell signals with the URL hash. Accepts
   * `#/projects/<slug>` and `#/projects/<slug>/<rail-key>`. Slug→name
   * resolution requires the workspace watch-paths; if they haven't
   * loaded yet we leave the signals alone — call again when they do.
   */
  syncShellFromHash(watchPaths: readonly { name: string }[]): void {
    const hash = window.location.hash;
    if (!hash.startsWith(this.shellHashPrefix)) {
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    const tail = hash.slice(this.shellHashPrefix.length);
    const [slugRaw, railRaw] = tail.split('/', 2);
    const slug = decodeURIComponent(slugRaw || '').toLowerCase();
    if (!slug) return;
    if (watchPaths.length === 0) return;
    const match = watchPaths.find(wp => toProjectSlug(wp.name) === slug);
    if (!match) {
      if (this.projectShellName() !== null) {
        this.projectShellName.set(null);
        this.projectShellRail.set(DEFAULT_PROJECT_RAIL_KEY);
      }
      return;
    }
    const railKey: ProjectRailKey = isProjectRailKey(railRaw) ? railRaw : DEFAULT_PROJECT_RAIL_KEY;
    if (this.projectShellName() !== match.name) this.projectShellName.set(match.name);
    if (this.projectShellRail() !== railKey) this.projectShellRail.set(railKey);
  }
}
