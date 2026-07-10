import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { OrchestratorLogEntry } from '../../../../features/orchestrator';
import { TaskService } from '../../../../services/task.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { GlobalOrchestratorCardComponent } from '../global-orchestrator-card/global-orchestrator-card';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Per-project orchestrator log feed. Reads
 * `/api/runner/{projectName}/orchestrator-log` on init and every 10s
 * while mounted. Renders a chronological list of entries: decisions,
 * actions (queued follow-ups, watchdog kills), observations,
 * interventions. The entry shape carries enough metadata for future
 * "override this decision" affordances (kept as a TODO note in the UI
 * but not wired today).
 *
 * Today's entries are written by the runner / watchdog. Phase D will
 * add an orchestrator-as-CLI process that writes its own reasoning
 * here with the same shape, so the feed stays one timeline.
 */
@Component({
  selector: 'app-orchestrator-feed',
  standalone: true,
  imports: [FormsModule, GlobalOrchestratorCardComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed.html',
  styleUrl: './orchestrator-feed.scss'
})
export class OrchestratorFeedComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly jobService = inject(TaskService);
  readonly entries = signal<OrchestratorLogEntry[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly kindFilter = signal<string>('signal');
  readonly projectFilter = signal<string>('all');
  readonly selectedEntry = signal<OrchestratorLogEntry | null>(null);
  /** Timestamp of the entry currently being overridden (one at a time). */
  readonly overridingTs = signal<string | null>(null);
  /** Submit-in-flight flag so the user cannot double-send. */
  readonly submittingOverride = signal(false);
  /** Two-way bound textarea draft. Cleared after each submit / cancel. */
  overrideDraft = '';
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly kindFilters = [
    { id: 'signal', label: 'Signal' },
    { id: 'decision', label: 'Decisions' },
    { id: 'action', label: 'Actions' },
    { id: 'observation', label: 'Observations' },
    { id: 'intervention', label: 'Interventions' },
    { id: 'all', label: 'All activity' }
  ];

  /** UI shows newest entries first; the on-disk log is oldest first. */
  readonly projects = computed(() => [...new Set(this.entries().map(entry => entry.project).filter(Boolean) as string[])].sort());
  readonly reversed = computed(() => [...this.entries()].sort((a, b) => b.ts.localeCompare(a.ts)));
  readonly visibleEntries = computed(() => {
    const filter = this.kindFilter();
    const project = this.projectFilter();
    return this.reversed().filter(entry =>
      (project === 'all' || entry.project === project)
      && (filter === 'all' || (filter === 'signal' ? entry.kind !== 'observation' : entry.kind === filter))
    );
  });
  readonly groupedEntries = computed(() => {
    const groups: { key: string; day: string; project: string; entries: OrchestratorLogEntry[] }[] = [];
    for (const entry of this.visibleEntries()) {
      const day = this.formatDay(entry.ts);
      const project = entry.project || this.projectName();
      const key = `${day}\u0000${project}`;
      let group = groups.find(item => item.key === key);
      if (!group) {
        group = { key, day, project, entries: [] };
        groups.push(group);
      }
      group.entries.push(entry);
    }
    return groups;
  });
  readonly countsByKind = computed(() => {
    const counts = new Map<string, number>();
    for (const entry of this.entries()) counts.set(entry.kind, (counts.get(entry.kind) ?? 0) + 1);
    return counts;
  });

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(true), 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    if (!silent) this.loading.set(true);
    this.jobService.getGlobalOrchestratorFeed().subscribe({
      next: (resp) => {
        const entries = resp.entries ?? [];
        this.entries.set(entries);
        const selected = this.selectedEntry();
        if (!selected && entries.length > 0) {
          this.selectedEntry.set(entries[entries.length - 1]);
        }
        this.error.set(null);
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        const message = err?.error?.error || err?.message || 'Failed to load orchestrator log';
        this.error.set(message);
        if (!silent) this.loading.set(false);
      }
    });
  }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'decision': return 'Decision';
      case 'action': return 'Action';
      case 'observation': return 'Observation';
      case 'intervention': return 'Intervention';
      default: return kind;
    }
  }

  filterCount(kind: string): number {
    if (kind === 'all') return this.entries().length;
    if (kind === 'signal') return this.entries().filter(entry => entry.kind !== 'observation').length;
    return this.countsByKind().get(kind) ?? 0;
  }

  selectFilter(kind: string): void {
    this.kindFilter.set(kind);
    const first = this.visibleEntries()[0] ?? null;
    this.selectedEntry.set(first);
  }

  selectEntry(entry: OrchestratorLogEntry): void {
    this.selectedEntry.set(entry);
  }

  selectProject(project: string): void {
    this.projectFilter.set(project);
    this.selectedEntry.set(this.visibleEntries()[0] ?? null);
  }

  navigateToTask(entry: OrchestratorLogEntry): void {
    if (!entry.jobId || !entry.watchPath) return;
    window.location.assign(`?job=${encodeURIComponent(entry.jobId)}&watchPath=${encodeURIComponent(entry.watchPath)}`);
  }

  isSelected(entry: OrchestratorLogEntry): boolean {
    const selected = this.selectedEntry();
    return !!selected && selected.ts === entry.ts && selected.summary === entry.summary;
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  formatDay(iso: string): string {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const today = new Date();
    if (date.toDateString() === today.toDateString()) return 'Today';
    return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  /**
   * Project colour-dot hue. Delegates to the shared project-identity util
   * (the AGT-2034 convention, an 11-hue curated palette) so a project's dot
   * in the feed matches the exact same hue it gets on board cards, studio
   * tabs, and task micro-cards. The former local hash produced an
   * off-palette hue that drifted from the rest of the app.
   */
  projectHue(project: string): number {
    return projectIdentity(project).hue;
  }

  startOverride(entry: OrchestratorLogEntry): void {
    this.overridingTs.set(entry.ts);
    this.overrideDraft = '';
  }

  cancelOverride(): void {
    this.overridingTs.set(null);
    this.overrideDraft = '';
  }

  submitOverride(entry: OrchestratorLogEntry): void {
    const direction = (this.overrideDraft ?? '').trim();
    if (!direction || !entry.jobId) return;
    this.submittingOverride.set(true);
    this.jobService.overrideOrchestratorEntry(entry.project || this.projectName(), {
      originalTs: entry.ts,
      jobId: entry.jobId,
      newDirection: direction
    }).subscribe({
      next: () => {
        this.submittingOverride.set(false);
        this.overridingTs.set(null);
        this.overrideDraft = '';
        // Refresh so the new intervention entry shows up.
        this.refresh(true);
      },
      error: (err) => {
        this.submittingOverride.set(false);
        const message = err?.error?.error || err?.message || 'Override failed';
        this.error.set(message);
      }
    });
  }

  tokenTooltip(tu: NonNullable<OrchestratorLogEntry['tokenUsage']>): string {
    return [
      `Model: ${tu.model || '?'}`,
      `Input: ${tu.inputTokens.toLocaleString()} tokens`,
      `Output: ${tu.outputTokens.toLocaleString()} tokens`,
      `Cache read: ${tu.cacheReadTokens.toLocaleString()} tokens`,
      `Cache creation: ${tu.cacheCreationTokens.toLocaleString()} tokens`
    ].join('\n');
  }
}
