import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { OrchestratorLogEntry } from '../../../../features/orchestrator';
import { TaskService } from '../../../../services/task.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { GlobalOrchestratorCardComponent } from '../global-orchestrator-card/global-orchestrator-card';
import { LoadDistributionComponent } from '../load-distribution/load-distribution.component';
import { OrchestratorFeedStore } from '../../state/orchestrator-feed.store';
import { OrchestratorFeedWindow } from './orchestrator-feed-windowing';

import { TooltipDirective } from 'coding-agent-chat/shared';
/** Workspace feed shared by the embedded main route and quick-access modal. */
@Component({
  selector: 'app-orchestrator-feed',
  standalone: true,
  imports: [FormsModule, GlobalOrchestratorCardComponent, LoadDistributionComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed.html',
  styleUrl: './orchestrator-feed.scss'
})
export class OrchestratorFeedComponent {
  readonly projectName = input.required<string>();
  readonly embedded = input(false);
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  private readonly jobService = inject(TaskService);
  readonly feedStore = inject(OrchestratorFeedStore);
  readonly entries = this.feedStore.entries;
  readonly loading = this.feedStore.loading;
  readonly error = this.feedStore.error;
  readonly kindFilter = signal<string>('signal');
  readonly projectFilter = signal<string>('all');
  readonly selectedEntry = signal<OrchestratorLogEntry | null>(null);
  readonly activeView = signal<'activity' | 'load'>('activity');
  readonly overridingTs = signal<string | null>(null);
  readonly submittingOverride = signal(false);
  overrideDraft = '';
  private readonly historyWindow = new OrchestratorFeedWindow();
  private readonly streamRef = viewChild<ElementRef<HTMLElement>>('stream');
  private anchorFrame: number | null = null;

  readonly kindFilters = [
    { id: 'signal', label: 'Signal' },
    { id: 'alert', label: 'Alerts' },
    { id: 'decision', label: 'Decisions' },
    { id: 'action', label: 'Actions' },
    { id: 'observation', label: 'Observations' },
    { id: 'intervention', label: 'Interventions' },
    { id: 'all', label: 'All activity' }
  ];

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
  readonly windowedEntries = computed(() => this.historyWindow.slice(this.visibleEntries()));
  readonly groupedEntries = computed(() => {
    const groups: { key: string; day: string; project: string; entries: OrchestratorLogEntry[] }[] = [];
    const byKey = new Map<string, typeof groups[number]>();
    for (const entry of this.windowedEntries()) {
      const day = this.formatDay(entry.ts);
      const project = entry.project || this.projectName();
      const key = `${day}\u0000${project}`;
      let group = byKey.get(key);
      if (!group) {
        group = { key, day, project, entries: [] };
        groups.push(group);
        byKey.set(key, group);
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
  readonly olderEntryCount = computed(() =>
    this.historyWindow.remaining(this.visibleEntries().length, this.windowedEntries().length)
  );

  private readonly selectionEffect = effect(() => {
    const entries = this.reversed();
    const selected = this.selectedEntry();
    if (selected && entries.includes(selected)) return;
    this.selectedEntry.set(entries[0] ?? null);
  });

  private readonly feedGrowthEffect = effect(() => {
    const scope = `${this.projectFilter()}\u0000${this.kindFilter()}`;
    const total = this.visibleEntries().length;
    const stream = this.streamRef()?.nativeElement;
    const followingNewest = !stream || stream.scrollTop <= 8;
    const beforeHeight = stream?.scrollHeight ?? 0;
    const beforeTop = stream?.scrollTop ?? 0;
    const grewBy = this.historyWindow.sync(scope, total, followingNewest);
    if (!stream || followingNewest || grewBy === 0 || typeof requestAnimationFrame === 'undefined') return;
    if (this.anchorFrame !== null) cancelAnimationFrame(this.anchorFrame);
    this.anchorFrame = requestAnimationFrame(() => {
      this.anchorFrame = null;
      stream.scrollTop = beforeTop + Math.max(0, stream.scrollHeight - beforeHeight);
    });
  });

  refresh(silent = false): void {
    this.feedStore.refresh(silent);
  }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'alert': return 'Alert';
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
    this.historyWindow.reset(`${this.projectFilter()}\u0000${kind}`);
    this.kindFilter.set(kind);
    const first = this.visibleEntries()[0] ?? null;
    this.selectedEntry.set(first);
  }

  selectEntry(entry: OrchestratorLogEntry): void {
    this.selectedEntry.set(entry);
  }

  selectProject(project: string): void {
    this.historyWindow.reset(`${project}\u0000${this.kindFilter()}`);
    this.projectFilter.set(project);
    this.selectedEntry.set(this.visibleEntries()[0] ?? null);
  }

  navigateToTask(entry: OrchestratorLogEntry): void {
    if (!entry.jobId || !entry.watchPath) return;
    this.openTask.emit({ jobId: entry.jobId, watchPath: entry.watchPath });
  }

  loadOlder(): void {
    this.historyWindow.loadOlder(this.olderEntryCount());
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
        this.feedStore.reportError(message);
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
