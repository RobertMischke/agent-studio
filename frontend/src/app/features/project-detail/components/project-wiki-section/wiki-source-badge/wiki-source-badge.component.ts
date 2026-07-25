import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { WikiSourceInfo } from '../../../../../models/project-docs.model';

@Component({
  selector: 'app-wiki-source-badge',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './wiki-source-badge.component.html',
  styleUrl: './wiki-source-badge.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WikiSourceBadgeComponent {
  readonly source = input.required<WikiSourceInfo>();
  readonly label = computed(() => {
    const source = this.source();
    return `${source.branch}${source.shortCommit ? ` @ ${source.shortCommit}` : ''}`;
  });
}
