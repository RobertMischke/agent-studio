import { ChangeDetectionStrategy, Component, OnInit, computed, output, signal, inject } from '@angular/core';
import { JobService } from '../../../../services/task.service';
import { CliConsoleComponent } from '../cli-console/cli-console';
import type { CliOutputLine, CliType } from '../../../../models/task.model';
import type {
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageReport,
  CliUsageSection,
  LinkedJobRef,
} from '../../../../features/cli';

import { TooltipDirective } from '../../../../components/tooltip';
interface SelectedSession {
  cliType: CliType;
  projectName: string;
  session: CliSessionInfo;
}

@Component({
  selector: 'app-cli-sessions-panel',
  standalone: true,
  imports: [CliConsoleComponent, TooltipDirective],
  templateUrl: './cli-sessions-panel.html',
  styleUrl: './cli-sessions-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliSessionsPanelComponent implements OnInit {
  private jobService = inject(JobService);

  /**
   * Emitted when the user clicks a session row's task-link chip. The shell
   * (via `orchestrator-side-sheet`) routes this through the existing
   * `JobSelectionService.openDetail` flow so the kanban detail panel
   * opens for the owning task. Mirrors the screenshot-strip `openTask`
   * pattern.
   */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly selected = signal<SelectedSession | null>(null);
  readonly collapsedClis = signal<Set<string>>(new Set());
  readonly expandedProjects = signal<Set<string>>(new Set());

  readonly visibleSections = computed<CliUsageSection[]>(() => {
    const all = this.report()?.sections ?? [];
    return all.filter((s) => s.available || s.projects.length > 0);
  });
  readonly totalSessionCount = computed<number>(() =>
    this.visibleSections().reduce((sum, s) => sum + this.totalSessions(s), 0),
  );
  readonly detailLines = computed<CliOutputLine[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    return [
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `Session: ${sel.session.id}` },
      { timestamp: new Date().toISOString(), stream: 'stdout', text: `CLI:     ${sel.cliType}` },
      {
        timestamp: new Date().toISOString(),
        stream: 'stdout',
        text: `Project: ${sel.projectName}`,
      },
      {
        timestamp: new Date().toISOString(),
        stream: 'stdout',
        text: sel.session.cwd ? `Cwd:     ${sel.session.cwd}` : '',
      },
    ].filter((l) => !!l.text);
  });

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.errorMsg.set(null);
    this.jobService.getCliUsageReport().subscribe({
      next: (r) => {
        this.report.set(r);
        this.loaded.set(true);
        this.loading.set(false);
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || 'Failed to load CLI sessions');
        this.loaded.set(true);
        this.loading.set(false);
      },
    });
  }

  toggleCli(cliType: string): void {
    const next = new Set(this.collapsedClis());
    if (next.has(cliType)) next.delete(cliType);
    else next.add(cliType);
    this.collapsedClis.set(next);
  }

  isCliCollapsed(cliType: string): boolean {
    return this.collapsedClis().has(cliType);
  }

  toggleProject(cliType: string, project: CliUsageProjectGroup): void {
    const key = this.projectKey(cliType, project);
    const next = new Set(this.expandedProjects());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedProjects.set(next);
  }

  isProjectCollapsed(cliType: string, project: CliUsageProjectGroup): boolean {
    return !this.expandedProjects().has(this.projectKey(cliType, project));
  }

  select(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo): void {
    this.selected.set({ cliType, projectName: project.projectName, session });
  }

  isSelected(cliType: CliType, project: CliUsageProjectGroup, session: CliSessionInfo): boolean {
    const sel = this.selected();
    return (
      !!sel &&
      sel.cliType === cliType &&
      sel.projectName === project.projectName &&
      sel.session.id === session.id
    );
  }

  totalSessions(section: CliUsageSection): number {
    return section.projects.reduce((sum, p) => sum + p.sessions.length, 0);
  }

  latestSession(project: CliUsageProjectGroup): CliSessionInfo | null {
    return project.sessions[0] ?? null;
  }

  usageSessionCount(project: CliUsageProjectGroup): number {
    return project.sessions.filter((s) => !!s.lastUsage).length;
  }

  cliLabel(t: string): string {
    switch (t) {
      case 'copilot':
        return 'Copilot';
      case 'claude':
        return 'Claude Code';
      case 'codex':
        return 'Codex';
      case 'gemini':
        return 'Gemini';
      default:
        return t;
    }
  }

  shortId(id: string): string {
    return id.length <= 12 ? id : id.slice(0, 8) + '...';
  }

  formatTime(iso: string): string {
    try {
      return new Date(iso).toLocaleString([], {
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return iso;
    }
  }

  private projectKey(cliType: string, project: CliUsageProjectGroup): string {
    return `${cliType}:${project.rootPath || project.projectName}`;
  }

  /**
   * Chip state for the task-link chip rendered next to a session row.
   * `'active'` and `'linked'` mirror the backend's two visible states;
   * `'none'` means no chip is rendered (orphan session).
   */
  chipState(linkedJob: LinkedJobRef | null | undefined): 'active' | 'linked' | 'none' {
    if (!linkedJob) return 'none';
    return linkedJob.isActive ? 'active' : 'linked';
  }

  /**
   * Plain-text tooltip body. Per the project tooltip rule (no HTML,
   * delayed open), this is a one-line string that the browser's native
   * title attribute renders.
   */
  chipTooltip(linkedJob: LinkedJobRef): string {
    const state = linkedJob.isActive ? 'active' : `linked (${linkedJob.lane})`;
    return `Open task: ${linkedJob.title} - ${state}`;
  }

  onOpenLinkedJob(event: Event, linkedJob: LinkedJobRef): void {
    // Stop the row's own click handler so the chip click does not also
    // select the session in the detail aside.
    event.stopPropagation();
    this.openJobDetail.emit({ jobId: linkedJob.jobId, watchPath: linkedJob.watchPath });
  }
}
