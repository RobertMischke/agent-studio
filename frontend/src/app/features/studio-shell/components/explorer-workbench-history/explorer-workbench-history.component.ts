import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  output,
  viewChildren,
} from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { WorkbenchListItem } from '../../../../models/project-docs.model';

@Component({
  selector: 'app-explorer-workbench-history',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-workbench-history.component.html',
  styleUrl: './explorer-workbench-history.component.scss',
})
export class ExplorerWorkbenchHistoryComponent {
  readonly projectName = input.required<string>();
  readonly items = input.required<readonly WorkbenchListItem[]>();
  readonly activeWorkbenchId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly groups = computed(() => [
    {
      status: 'documented', label: 'Documented', empty: 'No documented items',
      items: this.items().filter(item => item.status === 'documented'),
    },
    {
      status: 'archived', label: 'Archived', empty: 'No archived items',
      items: this.items().filter(item => item.status === 'archived'),
    },
  ]);
  private readonly topics = viewChildren<ElementRef<HTMLButtonElement>>('workbenchTopic');

  constructor() {
    effect(() => {
      const activeWorkbenchId = this.activeWorkbenchId();
      const activeTopic = this.topics()
        .map(topic => topic.nativeElement)
        .find(topic => topic.getAttribute('aria-current') === 'page');
      if (!activeWorkbenchId || !activeTopic || typeof activeTopic.scrollIntoView !== 'function') return;
      queueMicrotask(() => activeTopic.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
    });
  }

  isActive(item: WorkbenchListItem): boolean {
    return item.id === this.activeWorkbenchId();
  }

  secondaryMeta(item: WorkbenchListItem): string {
    return item.status === 'documented' ? 'Documented' : 'Archived';
  }

  tooltip(item: WorkbenchListItem): string {
    return [item.key, item.title, this.secondaryMeta(item)].filter(Boolean).join(' · ');
  }
}
