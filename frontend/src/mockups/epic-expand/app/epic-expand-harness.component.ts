import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import { TaskCardComponent } from '../../../app/features/board/components/task-card/task-card.component';
import type { TaskInfo } from '../../../app/models/task.model';

/**
 * Backend-free visual harness for ASS-1734 (epic inline-expand jumpy / loses
 * state). It mounts the *real* `TaskCardComponent` against a hand-seeded epic +
 * sub-tasks, so the fix (id-keyed `EpicExpansionStore` + stable `track sub.id`
 * + calm reveal) is captured exactly as the board paints it.
 *
 * The "Simulate poll refresh" button replaces the bound `TaskInfo` objects with
 * brand-new instances carrying the SAME ids (exactly what a board poll does).
 * Before the fix the expand collapsed on this refresh; after the fix it stays
 * open and the sub-list is reused (no double mount), which the screenshots show.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-seed',
    taskKey: 'demo::task-seed',
    title: 'Seed task',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/demo/watch',
    projectName: 'Demo',
    folderPath: '/demo/watch/3-progress/task-seed',
    lastActivity: '2026-06-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

const EPIC_ID = 'epic-aufklappen';
const WATCH = '/demo/watch';

function buildEpic(refreshTag: string): TaskInfo {
  return makeTask({
    id: EPIC_ID,
    taskKey: 'demo::epic-aufklappen',
    title: 'EPIC: Task-View Aufklappen',
    kind: 'epic',
    state: '2-ready',
    watchPath: WATCH,
    lastActivity: refreshTag,
  });
}

function buildSubTasks(refreshTag: string): TaskInfo[] {
  return [
    makeTask({
      id: 'sub-a',
      taskKey: 'demo::sub-a',
      title: 'Sub-task A — reproduce the jumpy expand',
      state: '3-progress',
      epicId: EPIC_ID,
      watchPath: WATCH,
      lastActivity: refreshTag,
    }),
    makeTask({
      id: 'sub-b',
      taskKey: 'demo::sub-b',
      title: 'Sub-task B — keyed expand store',
      state: '4-auto-review',
      epicId: EPIC_ID,
      watchPath: WATCH,
      lastActivity: refreshTag,
    }),
    makeTask({
      id: 'sub-c',
      taskKey: 'demo::sub-c',
      title: 'Sub-task C — stable trackBy on sub.id',
      state: '6-done',
      epicId: EPIC_ID,
      watchPath: WATCH,
      lastActivity: refreshTag,
    }),
  ];
}

@Component({
  selector: 'mockup-epic-expand-harness',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskCardComponent],
  templateUrl: './epic-expand-harness.component.html',
  styleUrl: './epic-expand-harness.component.scss',
})
export class EpicExpandHarnessComponent {
  private refreshes = 0;
  readonly epic = signal<TaskInfo>(buildEpic('2026-06-10T09:30:00Z'));
  readonly subTasks = signal<readonly TaskInfo[]>(buildSubTasks('2026-06-10T09:30:00Z'));
  readonly refreshCount = signal(0);

  simulateRefresh(): void {
    this.refreshes += 1;
    const tag = new Date(Date.parse('2026-06-10T09:30:00Z') + this.refreshes * 30_000).toISOString();
    // Brand-new objects, same ids — exactly what a board poll snapshot does.
    this.epic.set(buildEpic(tag));
    this.subTasks.set(buildSubTasks(tag));
    this.refreshCount.set(this.refreshes);
  }
}
