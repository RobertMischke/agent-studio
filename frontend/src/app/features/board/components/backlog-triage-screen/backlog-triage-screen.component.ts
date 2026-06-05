import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from '../../../../components/tooltip';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { ClientService } from '../../../../services/client.service';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import { projectIdentity } from '../../../../services/project-identity.util';
import { TaskService } from '../../../../services/task.service';
import { ErrorDialogService } from '../../../../services/error-dialog.service';
import { NotificationService } from '../../../../services/notification.service';
import type { TaskInfo, TagRegistryEntry } from '../../../../models/task.model';
import { BoardFiltersService } from '../../state/board-filters.service';
import {
  BacklogTriageService,
  type BacklogSortMode,
} from '../../state/backlog-triage.service';
import { BoardMutationsService } from '../../state/board-mutations.service';
import { FiltersDropdownComponent, type TypeFilterOption } from '../filters-dropdown/filters-dropdown.component';

interface SortOption {
  value: BacklogSortMode;
  label: string;
}

const TYPE_OPTIONS: readonly TypeFilterOption[] = [
  { value: 'bug', label: 'Bugs', icon: '🐞', kind: 'bug' },
  { value: 'feature', label: 'Features', icon: '✨', kind: 'feature' },
  { value: 'chore', label: 'Chores', icon: '·', kind: 'chore' },
];

const TYPE_ORDER: Record<string, number> = { bug: 0, feature: 1, chore: 2 };

/**
 * Dedicated triage screen for `0-backlog` jobs at `#/backlog`. Replaces
 * the horizontal kanban lane scan with a vertical list and per-row
 * quick-actions (Promote → Preparation, Promote → Ready, Edit tags,
 * Delete). The host owns the API mutations; this component renders +
 * emits.
 */
@Component({
  selector: 'app-backlog-triage-screen',
  standalone: true,
  imports: [TooltipDirective, FiltersDropdownComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './backlog-triage-screen.component.html',
  styleUrl: './backlog-triage-screen.component.scss',
})
export class BacklogTriageScreenComponent implements OnDestroy {
  private readonly boardFilters = inject(BoardFiltersService);
  private readonly triage = inject(BacklogTriageService);
  private readonly tagStore = inject(TagRegistryStore);
  private readonly clientService = inject(ClientService);
  private readonly boardMutations = inject(BoardMutationsService);
  private readonly jobService = inject(TaskService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly notifications = inject(NotificationService);

  readonly typeFilterOptions = TYPE_OPTIONS;
  readonly sortOptions: readonly SortOption[] = [
    { value: 'newest', label: 'Newest' },
    { value: 'oldest', label: 'Oldest' },
    { value: 'by-type', label: 'By type' },
  ];

  /**
   * Only the "+ New task" affordance bubbles to the host because the
   * create dialog's form state is owned by CreateTaskFormService and
   * the dialog is mounted by the shell. Other mutations (promote /
   * delete / set-tags) call into the existing services directly so
   * the shell stays a thin coordinator. The screen is a first-class
   * editor tab now, so there is no close/back affordance — navigation
   * is a tab switch.
   */
  readonly newTaskRequested = output<void>();

  /** Tick once per minute so relative-age strings stay fresh without flooding CD. */
  private readonly nowMs = signal(Date.now());
  private readonly nowInterval: ReturnType<typeof setInterval> | null =
    typeof window === 'undefined'
      ? null
      : setInterval(() => this.nowMs.set(Date.now()), 60_000);

  /** Row whose inline tag editor is currently expanded, by taskKey. */
  readonly tagEditorOpen = signal<string | null>(null);
  /** Local draft of the tag set while the editor is open. */
  readonly tagDraft = signal<Set<string>>(new Set());

  /** Filter dropdown bindings — same source-of-truth as the kanban. */
  readonly activeType = this.boardFilters.activeType;
  readonly activeTagFilter = this.boardFilters.activeTagFilter;
  readonly tagRegistry = this.tagStore.tags;
  readonly clients = computed(() => this.clientService.clients());

  readonly hasActiveFilters = this.boardFilters.hasActiveFilters;
  readonly hasActiveFiltersOrSearch = this.boardFilters.hasActiveFiltersOrSearch;
  readonly sortMode = this.triage.sortMode;
  readonly scopedProject = this.triage.scopedProject;

  readonly tagsById = this.tagStore.byId;

  /** Backlog jobs after BoardFiltersService narrows by the explicit page project. */
  readonly filteredBacklog = computed<TaskInfo[]>(
    () => this.boardFilters.filteredGroupedForProject(this.scopedProject()).backlog ?? [],
  );

  /** Sorted list driven by the persisted sort mode. */
  readonly visibleJobs = computed<TaskInfo[]>(() => {
    const list = this.filteredBacklog().slice();
    const mode = this.sortMode();
    if (mode === 'newest') {
      list.sort((a, b) => compareCreated(b, a));
    } else if (mode === 'oldest') {
      list.sort((a, b) => compareCreated(a, b));
    } else {
      list.sort((a, b) => {
        const ta = TYPE_ORDER[a.taskType || 'chore'] ?? 99;
        const tb = TYPE_ORDER[b.taskType || 'chore'] ?? 99;
        if (ta !== tb) return ta - tb;
        return compareCreated(b, a);
      });
    }
    return list;
  });

  ngOnDestroy(): void {
    if (this.nowInterval !== null) clearInterval(this.nowInterval);
  }

  setSort(value: BacklogSortMode): void {
    this.triage.setSortMode(value);
  }

  onSetType(value: string | null): void {
    this.boardFilters.onSetType(value);
  }

  onToggleTag(id: string): void {
    this.boardFilters.toggleTagFilter(id);
  }

  onClearFilters(): void {
    this.boardFilters.clearAllFilters();
  }

  promote(task: TaskInfo, target: '1-preparation' | '2-ready'): void {
    this.boardMutations.changeStateFromDetail(task, target);
  }

  onDelete(task: TaskInfo): void {
    this.boardMutations.deleteFromBoard(task);
  }

  identityFor(name: string) {
    return projectIdentity(name);
  }

  ownerChip(task: TaskInfo): { label: string; emoji: string; color: string | null } | null {
    const id = task.ownerClientId;
    if (!id) return null;
    const c = this.clientService.resolve(id);
    return { label: c.displayName || id, emoji: c.emoji || '·', color: c.colour ?? null };
  }

  taskTypeChip(task: TaskInfo): { kind: string; label: string; icon: string } {
    const raw = (task.taskType || 'chore').toLowerCase();
    const normalised = raw === 'user-story' ? 'feature' : raw;
    switch (normalised) {
      case 'bug':
        return { kind: 'bug', label: 'Bug', icon: '🐞' };
      case 'feature':
        return { kind: 'feature', label: 'Feature', icon: '✨' };
      default:
        return { kind: 'chore', label: 'Chore', icon: '·' };
    }
  }

  tagChipsFor(task: TaskInfo): { id: string; label: string; color: string | null; isGhost: boolean }[] {
    const ids = task.tags ?? [];
    const byId = this.tagsById();
    return ids.map(id => {
      const entry = byId.get(id);
      return {
        id,
        label: entry?.label ?? id,
        color: entry?.color ?? null,
        isGhost: !entry,
      };
    });
  }

  relativeAge(dateStr: string): string {
    if (!dateStr) return '';
    const now = this.nowMs();
    const ms = now - new Date(dateStr).getTime();
    if (ms < 0 || Number.isNaN(ms)) return 'just now';
    const sec = Math.floor(ms / 1000);
    if (sec < 60) return 'just now';
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min}m ago`;
    const hrs = Math.floor(min / 60);
    if (hrs < 48) return `${hrs}h ago`;
    const days = Math.floor(hrs / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.floor(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.floor(months / 12)}y ago`;
  }

  toggleTagEditor(task: TaskInfo): void {
    const key = task.taskKey;
    if (this.tagEditorOpen() === key) {
      this.tagEditorOpen.set(null);
      this.tagDraft.set(new Set());
      return;
    }
    this.tagEditorOpen.set(key);
    this.tagDraft.set(new Set(task.tags ?? []));
  }

  toggleDraftTag(id: string): void {
    const next = new Set(this.tagDraft());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.tagDraft.set(next);
  }

  isTagInDraft(id: string): boolean {
    return this.tagDraft().has(id);
  }

  saveTagDraft(task: TaskInfo): void {
    const tags = [...this.tagDraft()];
    this.tagEditorOpen.set(null);
    this.tagDraft.set(new Set());
    this.jobService.setJobTags(task.id, tags, task.watchPath).subscribe({
      next: () => {
        this.jobService.refresh();
        this.notifications.success(
          `Tags updated on "${task.title || task.id}"`,
          'Backlog triage',
        );
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to update tags',
          fallbackMessage: 'Failed to update tags',
          source: `Task ${task.id}`,
        });
      },
    });
  }

  cancelTagDraft(): void {
    this.tagEditorOpen.set(null);
    this.tagDraft.set(new Set());
  }

  newTask(): void {
    this.newTaskRequested.emit();
  }

  trackByKey = (_: number, task: TaskInfo) => task.taskKey;
  trackByTagId = (_: number, t: TagRegistryEntry) => t.id;
}

function compareCreated(a: TaskInfo, b: TaskInfo): number {
  const ta = Date.parse(a.createdAt ?? '');
  const tb = Date.parse(b.createdAt ?? '');
  const va = Number.isNaN(ta) ? 0 : ta;
  const vb = Number.isNaN(tb) ? 0 : tb;
  return va - vb;
}
