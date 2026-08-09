import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { ConnectedOverlayDirective } from '../../../../directives/connected-overlay.directive';
import { WorkbenchDocument } from '../../../../models/project-docs.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { TaskService } from '../../../../services/task.service';
import { PageActionBarComponent } from '../page-action-bar/page-action-bar';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import { PageContext } from '../../../../models/page-context.model';

const MAX_INLINE_TASKS = 8;

@Component({
  selector: 'app-workbench-viewer-header',
  standalone: true,
  imports: [
    AppTooltipDirective,
    ConnectedOverlayDirective,
    CopyableTaskKeyComponent,
    PageActionBarComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchDecisionPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer-header.component.html',
  styleUrl: './workbench-viewer-header.component.scss',
})
export class WorkbenchViewerHeaderComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly pageContext = input.required<PageContext>();
  readonly openWiki = output<void>();
  readonly decisionChanged = output<void>();

  private readonly tasks = inject(TaskService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly detailsTrigger = viewChild<ElementRef<HTMLButtonElement>>('detailsTrigger');
  private readonly detailsPopover = viewChild<ElementRef<HTMLElement>>('detailsPopover');

  readonly detailsOpen = signal(false);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);
  readonly taskStatusesLoading = signal(false);
  readonly triggerElement = computed(() => this.detailsTrigger()?.nativeElement ?? null);

  readonly referenceKeys = computed(() => {
    const workbench = this.document().workbench;
    const canonicalKey = normalizeKey(workbench.key);
    const derived: string[] = [];
    if (canonicalKey) {
      for (const task of this.tasks.jobs()) {
        if (!(task.references?.workbenches ?? []).some(key => normalizeKey(key) === canonicalKey)) continue;
        if (task.key) derived.push(task.key);
      }
    }
    return uniqueKeys([...derived, ...(workbench.relatedTaskKeys ?? [])]);
  });
  readonly inlineTaskStatuses = computed(() => this.taskStatuses().slice(0, MAX_INLINE_TASKS));
  readonly hiddenTaskCount = computed(() => Math.max(0, this.taskStatuses().length - MAX_INLINE_TASKS));
  readonly openDecisionCount = computed(() => {
    const workbench = this.document().workbench;
    const projected = workbench.openDecisionCount;
    if (typeof projected === 'number' && Number.isFinite(projected)) return Math.max(0, projected);
    if (workbench.decision?.state === 'succeeded' || workbench.status === 'decided' || workbench.status === 'archived') return 0;
    return workbench.status === 'decision-pending' ? 1 : 0;
  });
  readonly statusLabel = computed(() => {
    const workbench = this.document().workbench;
    return [humanize(workbench.status), workbench.phase ? humanize(workbench.phase) : null]
      .filter(Boolean)
      .join(' · ');
  });

  constructor() {
    effect(onCleanup => {
      const keys = this.referenceKeys();
      if (keys.length === 0) {
        this.taskStatuses.set([]);
        this.taskStatusesLoading.set(false);
        return;
      }
      this.taskStatusesLoading.set(true);
      const subscription = this.tasks.getReferenceStatuses(keys).subscribe({
        next: statuses => {
          const byKey = new Map(statuses.map(status => [normalizeKey(status.key), status]));
          this.taskStatuses.set(keys.map(key => byKey.get(normalizeKey(key)) ?? ghostStatus(key, this.projectName())));
          this.taskStatusesLoading.set(false);
        },
        error: () => {
          this.taskStatuses.set(keys.map(key => ghostStatus(key, this.projectName())));
          this.taskStatusesLoading.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });

    effect(() => {
      if (!this.document().workbench.id) return;
      this.detailsOpen.set(false);
    });

    let dispose: (() => void) | null = null;
    effect(() => {
      if (this.detailsOpen() && !dispose) {
        dispose = this.modalStack.push('viewer-details', () => this.closeDetails());
      } else if (!this.detailsOpen() && dispose) {
        dispose();
        dispose = null;
      }
    });
    this.destroyRef.onDestroy(() => dispose?.());
  }

  toggleDetails(event: Event): void {
    event.stopPropagation();
    this.detailsOpen.update(open => !open);
  }

  closeDetails(): void {
    this.detailsOpen.set(false);
  }

  @HostListener('document:click', ['$event'])
  closeDetailsOnOutsideClick(event: MouseEvent): void {
    if (!this.detailsOpen() || !(event.target instanceof Node)) return;
    if (this.host.nativeElement.contains(event.target)) return;
    if (this.detailsPopover()?.nativeElement.contains(event.target)) return;
    this.closeDetails();
  }
}

function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

function humanize(value: string): string {
  const words = value.replaceAll('-', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
