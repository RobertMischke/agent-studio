import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import { TaskCardComponent } from '../../../app/features/board';
import { TaskState } from '../../../app/models/task.model';
import type { TaskInfo } from '../../../app/models/task.model';

/**
 * AGT-2020 visual harness — "Delete: Hover-Icon → Kontextmenü".
 *
 * It mounts the *real* `TaskCardComponent` (task + epic) so the reviewer can
 * eyeball, in both themes, the two halves of the change exactly as the board
 * paints them:
 *  - the card carries **no** hover trash button any more (right-click / Menu
 *    key is the only delete affordance), and
 *  - the card's own right-click context menu ends in a destructive, danger-
 *    styled "Delete task" / "Delete epic" row behind a separator.
 *
 * Backend-free: the card is seeded from a hand-built `TaskInfo` and its
 * best-effort epic lookup simply resolves to "No epics" without a backend —
 * the precedent set by the other `src/mockups/*` harnesses.
 *
 * The theme buttons flip `html[data-studio-theme]` (dark is the app default,
 * light is the opt-in) so a single harness proves both palettes.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-seed',
    taskKey: 'demo::task-seed',
    key: 'ATP-1',
    title: 'Task-Karte: Löschen ins Kontextmenü',
    state: TaskState.Progress,
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/demo/watch',
    projectName: 'Demo',
    folderPath: '/demo/watch/3-progress/task-seed',
    lastActivity: '2026-07-09T09:30:00Z',
    sessionName: null,
    model: 'opus-4.8',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  } as TaskInfo;
}

type Theme = 'dark' | 'light';

@Component({
  selector: 'mockup-task-card-delete-harness',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskCardComponent],
  templateUrl: './task-card-delete-harness.component.html',
  styleUrl: './task-card-delete-harness.component.scss',
})
export class TaskCardDeleteHarnessComponent {
  readonly theme = signal<Theme>('dark');

  readonly task = signal<TaskInfo>(makeTask());
  readonly epic = signal<TaskInfo>(
    makeTask({
      id: 'epic-seed',
      taskKey: 'demo::epic-seed',
      key: 'ATP-9',
      title: 'EPIC: Board-Interaktionen aufräumen',
      kind: 'epic',
      state: TaskState.Ready,
      folderPath: '/demo/watch/2-ready/epic-seed',
    }),
  );

  setTheme(theme: Theme): void {
    this.theme.set(theme);
    const root = document.documentElement;
    if (theme === 'light') {
      root.setAttribute('data-studio-theme', 'light');
    } else {
      root.removeAttribute('data-studio-theme');
    }
  }
}
