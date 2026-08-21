import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { cliTypeIcon, cliTypeLabel, formatDateTime } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import type { CliUsageReport, CliUsageSection } from '../../models/cli.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';

/** One filesystem root a CLI holds session state for. */
interface CliPathRow {
  projectName: string;
  rootPath: string;
  sessionCount: number;
}

/** One CLI's on-disk footprint: where its binary lives and which project
 *  roots it has session state for. */
interface CliPathGroup {
  cliType: CliType;
  label: string;
  icon: string;
  available: boolean;
  version: string | null;
  executablePath: string | null;
  error: string | null;
  roots: CliPathRow[];
  /** Most recent npm-shim self-heal pass for this CLI (AGT-2673), if any. */
  lastRepairAt: string | null;
  lastRepairDiagnosis: string | null;
  lastRepairSucceeded: boolean | null;
}

/**
 * "CLI paths" settings page (AGT-2101). A read-only, encapsulated view of where
 * each CLI lives on this host: its discovered executable path and version, plus
 * the project roots it holds native session state for. Both come from the same
 * `/api/cli/usage` report the CLI-sessions inventory reads, so opening this page
 * needs no new backend surface - it is the filesystem-location projection of
 * that report, split out of the CLI Management hub so each page stays focused.
 */
@Component({
  selector: 'app-cli-paths-panel',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './cli-paths-panel.html',
  styleUrl: './cli-paths-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliPathsPanelComponent implements OnInit {
  private readonly jobService = inject(TaskService);

  readonly report = signal<CliUsageReport | null>(null);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly errorMsg = signal<string | null>(null);

  readonly groups = computed<CliPathGroup[]>(() => {
    const sections = this.report()?.sections ?? [];
    // A CLI with no local session history and a currently-failed repair
    // (AGT-2673) still belongs on this page - "available || has sessions"
    // alone would silently drop a freshly-broken CLI with an empty history.
    return sections
      .filter((s) => s.available || s.projects.length > 0 || !!s.lastRepairAt)
      .map((s) => this.toGroup(s));
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
        this.errorMsg.set(err?.error?.error || err?.message || 'Failed to load CLI paths');
        this.loaded.set(true);
        this.loading.set(false);
      },
    });
  }

  /** "Repaired at <time> (shim missing package present)" / "Repair failed at <time> (…)".
   *  Non-silent surfacing of the AGT-2673 npm-shim self-heal: a repair note is only ever
   *  shown when one actually ran - the common case (nothing was ever broken) shows nothing. */
  repairNote(g: CliPathGroup): string | null {
    if (!g.lastRepairAt) return null;
    const when = formatDateTime(g.lastRepairAt);
    const diagnosis = (g.lastRepairDiagnosis ?? '').replace(/-/g, ' ');
    return g.lastRepairSucceeded
      ? `Repaired at ${when} (${diagnosis})`
      : `Repair failed at ${when} (${diagnosis})`;
  }

  private toGroup(section: CliUsageSection): CliPathGroup {
    const roots: CliPathRow[] = section.projects
      .map((p) => ({
        projectName: p.projectName,
        rootPath: p.rootPath ?? '',
        sessionCount: p.sessions.length,
      }))
      .filter((r) => !!r.rootPath)
      .sort((a, b) => a.projectName.localeCompare(b.projectName));
    return {
      cliType: section.cliType,
      label: cliTypeLabel(section.cliType),
      icon: cliTypeIcon(section.cliType),
      available: section.available,
      version: section.version,
      executablePath: section.path,
      error: section.error,
      roots,
      lastRepairAt: section.lastRepairAt ?? null,
      lastRepairDiagnosis: section.lastRepairDiagnosis ?? null,
      lastRepairSucceeded: section.lastRepairSucceeded ?? null,
    };
  }
}
