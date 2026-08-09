import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import { TaskCardComponent } from '../../../app/features/board';
import { PlanningSpawnPanelComponent } from '../../../app/features/task-detail';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../app/components/task-reference-microcard/task-reference-microcard';
import { TaskState } from '../../../app/models/task.model';
import type { PlanningSpawnSummary, TaskInfo } from '../../../app/models/task.model';

/**
 * AGT-2069 visual harness for planning-task visibility ("krass sichtbar: hier
 * wird GEPLANT") on the *real* components, in both themes and backend-free:
 *
 *  1. Board card mode badge (real `TaskCardComponent`) — a planning card carries
 *     a loud violet "PLANNING" pill, a research card a teal "RESEARCH" pill, and
 *     a coding card stays quiet. Requirement A.
 *  2. Spawn microcard (real `TaskReferenceMicrocardComponent`, AGT-2050) — the
 *     "spawnt: AGT-xxxx" chip a planning task shows for each follow-up it
 *     created. Requirement B.
 *  3. Planning spawn panel (real `PlanningSpawnPanelComponent`) in its three
 *     spawn-contract states: follow-ups spawned, the compact unresolved status
 *     with both resolution actions, and a deliberate no-follow-up declaration.
 *
 * The badges and panel are pure functions of the seeded `TaskInfo.mode` /
 * `TaskInfo.planningSpawn`, so no live backend is needed — the precedent set by
 * the other `src/mockups/*` galleries.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'agt-2069-plan',
    taskKey: 'agt::agt-2069-plan',
    key: 'AGT-2069',
    title: 'Planning-Tasks krass sichtbar + Spawn-Kontrakt',
    state: TaskState.HumanReview,
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-10T09:00:00Z',
    watchPath: '/ws/agt',
    projectName: 'AGT',
    folderPath: '/ws/agt/5-human-review/agt-2069-plan',
    lastActivity: '2026-07-10T18:30:00Z',
    sessionName: null,
    model: 'opus-4.8',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    mode: 'coding',
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    ...overrides,
  } as TaskInfo;
}

interface CardScenario {
  id: string;
  label: string;
  note: string;
  job: TaskInfo;
}

const CARD_SCENARIOS: readonly CardScenario[] = [
  {
    id: 'planning',
    label: 'Planning card (mode: planning)',
    note: 'A read-only planning run. The loud violet "PLANNING" pill says "hier wird GEPLANT" before the card is ever opened.',
    job: makeTask({
      mode: 'planning',
      title: 'Plan: Pipeline-Workbench Phase 3 zuschneiden',
    }),
  },
  {
    id: 'research',
    label: 'Research card (mode: research)',
    note: 'A read-only research run (web access on). The teal "RESEARCH" pill distinguishes fact-finding from planning.',
    job: makeTask({
      key: 'AGT-2070',
      mode: 'research',
      title: 'Research: Optionen fuer verteilte Runner-Hosts',
    }),
  },
  {
    id: 'coding',
    label: 'Coding card (mode: coding) — contrast',
    note: 'The common case stays quiet: no mode badge, so the board is not noisy and the planning/research cards stand out.',
    job: makeTask({
      key: 'AGT-2067',
      mode: 'coding',
      title: 'Umsetzung: 1915-Konzept in iframe-Split-View',
    }),
  },
];

const SPAWN_MICROCARD: TaskReferenceStatus = {
  key: 'AGT-2067',
  exists: true,
  taskKey: 'agt::agt-2067',
  title: 'Umsetzung: 1915-Konzept in iframe-Split-View',
  lane: '6-completed',
  projectId: 'agt',
  projectName: 'AGT',
  projectColor: '#f0883e',
  merge: null,
  reviewGrade: 'A',
};

function summary(overrides: Partial<PlanningSpawnSummary>): PlanningSpawnSummary {
  return {
    spawned: [],
    spawnedCount: 0,
    noFollowUpDeclared: false,
    contractSatisfied: false,
    ...overrides,
  };
}

interface PanelScenario {
  id: string;
  label: string;
  note: string;
  job: TaskInfo;
}

const PANEL_SCENARIOS: readonly PanelScenario[] = [
  {
    id: 'spawned',
    label: 'Follow-ups spawned — contract met',
    note: 'The planning run created concrete follow-up cards. The panel lists them as "spawnt: AGT-xxxx" microcards, so the outcome needs no extra contract badge.',
    job: makeTask({
      mode: 'planning',
      planningSpawn: summary({
        spawned: [
          { targetKey: 'AGT-2067', targetJobId: 'agt-2067', targetProject: 'AGT', reason: 'Implement the 1915 concept', at: '2026-07-10T18:00:00Z' },
          { targetKey: 'AGT-2062', targetJobId: 'agt-2062', targetProject: 'AGT', reason: 'Workbench plan phase 2', at: '2026-07-10T18:05:00Z' },
        ],
        spawnedCount: 2,
        contractSatisfied: true,
      }),
    }),
  },
  {
    id: 'at-risk',
    label: 'No follow-ups, none declared — the AGT-1915 trap',
    note: 'Nothing spawned and nothing declared: one quiet warning status and the two available resolution actions, without a duplicate header badge or teaching box.',
    job: makeTask({
      mode: 'planning',
      planningSpawn: summary({ contractSatisfied: false }),
    }),
  },
  {
    id: 'declared',
    label: 'Deliberate "no follow-up intended" — contract met',
    note: 'Sometimes a plan concludes no work should follow. Declared explicitly (with an optional reason), the contract is satisfied — a deliberate call, never a silent slip.',
    job: makeTask({
      mode: 'planning',
      planningSpawn: summary({
        noFollowUpDeclared: true,
        noFollowUpReason: 'Concept superseded by AGT-2062; deliberately no code change.',
        declaredAt: '2026-07-10T18:20:00Z',
        contractSatisfied: true,
      }),
    }),
  },
];

type Theme = 'dark' | 'light';

@Component({
  selector: 'mockup-planning-visibility-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskCardComponent, TaskReferenceMicrocardComponent, PlanningSpawnPanelComponent],
  templateUrl: './planning-visibility-gallery.component.html',
  styleUrl: './planning-visibility-gallery.component.scss',
})
export class PlanningVisibilityGalleryComponent {
  readonly theme = signal<Theme>('dark');
  readonly cardScenarios = CARD_SCENARIOS;
  readonly panelScenarios = PANEL_SCENARIOS;
  readonly microcard = SPAWN_MICROCARD;

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
