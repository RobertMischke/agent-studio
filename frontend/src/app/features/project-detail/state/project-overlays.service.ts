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
 *   - orchestrator-feed (per-project log)          no hash
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

  // ---------- orch-feed ----------

  openOrchFeed(name: string): void {
    this.orchFeedProject.set(name);
  }

  closeOrchFeed(): void {
    this.orchFeedProject.set(null);
  }

  // ---------- project-shell (URL-deep-linked) ----------

  openProjectShell(name: string, rail: ProjectRailKey = DEFAULT_PROJECT_RAIL_KEY,
                   watchPaths: ReadonlyArray<{ name: string }> = []): void {
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
  syncShellFromHash(watchPaths: ReadonlyArray<{ name: string }>): void {
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
