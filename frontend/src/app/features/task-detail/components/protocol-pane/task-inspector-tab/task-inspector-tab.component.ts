import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import type { TaskRefinementEntry } from '../../../../run-timeline';
import { resolveProtocolImageSrc } from '../protocol-image-resolver';

@Component({
  selector: 'app-task-inspector-tab',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownViewComponent],
  templateUrl: './task-inspector-tab.component.html',
  styleUrl: './task-inspector-tab.component.scss',
})
export class TaskInspectorTabComponent {
  readonly promptMarkdown = input<string>('');
  readonly refinements = input<readonly TaskRefinementEntry[]>([]);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly imageResolver = computed<(src: string) => string>(() => {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return (src: string) => resolveProtocolImageSrc(src, jobId, watchPath);
  });

  actorLabel(actor: TaskRefinementEntry['actor']): string {
    switch (actor) {
      case 'operator': return 'Operator';
      case 'agent': return 'Agent';
      default: return 'System';
    }
  }

  formatClock(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime())
      ? iso
      : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  formatDateTime(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
  }
}
