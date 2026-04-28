import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export interface ProjectRunnerIndicator { icon: string; cls: string; }

export interface ProjectAutoInfo {
  /** off = manual / paused-without-active. on = auto-continuous. stopping = paused while a task is still running. */
  state: 'off' | 'on' | 'stopping';
  readyCount: number;
  icon: string;
  label: string;
  tooltip: string;
}

/**
 * Header chip strip showing each watched project. Clicks toggle the
 * project filter; a runner indicator shows when a project's pipeline
 * is active. Each chip is paired with an Auto-pickup toggle that
 * enables/disables continuous pickup of Ready tasks for that project.
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
  readonly autoInfo = input.required<(name: string) => ProjectAutoInfo>();

  readonly toggle = output<string>();
  readonly toggleAuto = output<string>();
}
