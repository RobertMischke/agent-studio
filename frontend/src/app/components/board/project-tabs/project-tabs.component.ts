import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export interface ProjectRunnerIndicator { icon: string; cls: string; }

/**
 * Header chip strip showing each watched project. Clicks toggle the
 * project filter; a runner indicator shows when a project's pipeline
 * is active.
 */
@Component({
  selector: 'app-project-tabs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-tabs.component.html'
})
export class ProjectTabsComponent {
  readonly names = input<string[]>([]);
  readonly isActive = input.required<(name: string) => boolean>();
  readonly runnerIndicator = input.required<(name: string) => ProjectRunnerIndicator | null>();

  readonly toggle = output<string>();
}
