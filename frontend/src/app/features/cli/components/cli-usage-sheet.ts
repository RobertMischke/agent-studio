import { Component, OnDestroy, OnInit, signal, computed } from '@angular/core';
import { JobService } from '../../../services/job.service';
import { QuotaStripComponent } from '../../quota/components/quota-strip';
import type { CliOutputLine, CliType } from '../../../models/job.model';
import type { CliSessionInfo, CliUsageProjectGroup, CliUsageReport, CliUsageSection } from '../../../features/cli';

interface SelectedSession {
  cliType: CliType;
  projectName: string;
  session: CliSessionInfo;
}

/**
 * Right-hand sidesheet that combines two collapsible segments:
 *   - Quota: subscription / rate-limit visualisation (rendered by QuotaStripComponent).
 *   - Sessions: per-CLI per-project session list with token usage and (best-effort) output.
 *
 * Unlike a typical floating sidesheet this component participates in normal
 * document flow — the parent layout in `app.ts` keeps it next to the main
 * content via flexbox. When closed the host width collapses to 0 so the rest
 * of the UI gets the space back without an overlay covering anything.
 *
 * Live session output streaming for Claude/Codex isn't wired yet; we surface
 * what's already on disk plus session metadata.
 */
@Component({
  selector: 'app-cli-usage-sheet',
  standalone: true,
  imports: [QuotaStripComponent],
  templateUrl: './cli-usage-sheet.html',
  styleUrl: './cli-usage-sheet.scss',
  host: {
    '[class.is-open]': 'open()'
  }
})
export class CliUsageSheetComponent implements OnInit, OnDestroy {
  readonly open = signal(false);
  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly selected = signal<SelectedSession | null>(null);
  // Per-CLI accordion state inside the Sessions segment.
  readonly collapsedClis = signal<Set<string>>(new Set());
  readonly expandedProjects = signal<Set<string>>(new Set());
  // Top-level segment accordion state. Quota stays expanded by default
  // because it's the highest-value glance information.
  readonly collapsedSegments = signal<Set<string>>(new Set());

  readonly visibleSections = computed<CliUsageSection[]>(() => {
    const all = this.report()?.sections ?? [];
    return all.filter(s => s.available || s.projects.length > 0);
  });
  readonly totalSessionCount = computed<number>(() =>
    this.visibleSections().reduce((sum, s) => sum + this.totalSessions(s), 0)
  );
  readonly detailLines = computed<CliOutputLine[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    return [
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `Session: ${sel.session.id}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `CLI:     ${sel.cliType}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `Project: ${sel.projectName}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: sel.session.cwd ? `Cwd:     ${sel.session.cwd}` : '' }
    ].filter(l => !!l.text);
  });

  closed = { emit: () => this.open.set(false) };
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  constructor(private jobService: JobService) {}

  show() { this.open.set(true); }
  hide() { this.open.set(false); }
  toggle() { this.open() ? this.hide() : this.show(); }

  ngOnInit() {
    // Sessions intentionally do not load from this usage sheet anymore.
    // The native CLI session scan is lazy in Orchestrator -> Sessions.
  }

  ngOnDestroy() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
  }

  refreshSessions(silent = false) {
    if (!silent) this.loading.set(true);
    this.errorMsg.set(null);
    this.jobService.getCliUsageReport().subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || 'Failed to load CLI usage');
        this.loading.set(false);
      }
    });
  }

  toggleSegment(name: 'quota' | 'sessions') {
    const next = new Set(this.collapsedSegments());
    if (next.has(name)) next.delete(name);
    else next.add(name);
    this.collapsedSegments.set(next);
  }
  isSegmentCollapsed(name: string): boolean { return this.collapsedSegments().has(name); }

  toggleCli(cliType: string) {
    const next = new Set(this.collapsedClis());
    if (next.has(cliType)) next.delete(cliType);
    else next.add(cliType);
    this.collapsedClis.set(next);
  }
  isCliCollapsed(cliType: string): boolean { return this.collapsedClis().has(cliType); }

  toggleProject(cliType: string, project: CliUsageProjectGroup) {
    const key = this.projectKey(cliType, project);
    const next = new Set(this.expandedProjects());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedProjects.set(next);
  }

  isProjectCollapsed(cliType: string, project: CliUsageProjectGroup): boolean {
    return !this.expandedProjects().has(this.projectKey(cliType, project));
  }

  private projectKey(cliType: string, project: CliUsageProjectGroup): string {
    return `${cliType}:${project.rootPath || project.projectName}`;
  }

  totalSessions(section: CliUsageSection): number {
    return section.projects.reduce((sum, p) => sum + p.sessions.length, 0);
  }

  latestSession(project: CliUsageProjectGroup): CliSessionInfo | null {
    return project.sessions[0] ?? null;
  }

  usageSessionCount(project: CliUsageProjectGroup): number {
    return project.sessions.filter(s => !!s.lastUsage).length;
  }

  select(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo) {
    this.selected.set({ cliType, projectName: project.projectName, session });
  }

  isSelected(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo): boolean {
    const sel = this.selected();
    return !!sel && sel.cliType === cliType && sel.projectName === project.projectName && sel.session.id === session.id;
  }

  cliLabel(t: string): string {
    switch (t) {
      case 'copilot': return 'Copilot';
      case 'claude':  return 'Claude Code';
      case 'codex':   return 'Codex';
      case 'gemini':  return 'Gemini';
      default:        return t;
    }
  }

  shortId(id: string): string {
    return id.length <= 12 ? id : id.slice(0, 8) + '…';
  }

  formatTime(iso: string): string {
    try { return new Date(iso).toLocaleString([], { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }); }
    catch { return iso; }
  }
}
