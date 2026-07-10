import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import { TaskCardComponent } from '../../../app/features/board';
import { TaskState } from '../../../app/models/task.model';
import type { TaskInfo, WaitsOnStatus } from '../../../app/models/task.model';

/**
 * AGT-2029 visual harness for the waits-on dependency chip on the board card
 * ("auf der Karte sichtbar"). It mounts the *real* `TaskCardComponent` for each
 * dependency state so the reviewer can eyeball, in both themes, exactly what the
 * board paints:
 *  - open cross-project (waits: CAR-3, ⏳) — the card is held back from pickup;
 *  - multiple open (waits: AGT-2025 +1) — summarised with a +N suffix;
 *  - fulfilled / ready (✓ CAR-3) — all dependencies complete, card is workable;
 *  - cycle (⚠ dep cycle) — a configuration error that can never be fulfilled;
 *  - unknown / not-yet-created (waits: GHOST-9, ⏳) — a target the operator may
 *    create later.
 *
 * Backend-free: the chip is a pure function of `TaskInfo.waitsOn` (computed
 * server-side, resolved cross-project + archive-inclusive), so no services,
 * HTTP, or SignalR are needed — the precedent set by the other src/mockups/*.
 *
 * The theme buttons flip `html[data-studio-theme]` (dark is the app default,
 * light is the opt-in) so a single harness proves both palettes.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'agt-audit',
    taskKey: 'agt::agt-audit',
    key: 'AGT-2029',
    title: 'Kosten-Audit: neue Pricing-Lib einziehen',
    state: TaskState.Ready,
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/ws/agt',
    projectName: 'AGT',
    folderPath: '/ws/agt/2-ready/agt-audit',
    lastActivity: '2026-07-09T09:30:00Z',
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
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    ...overrides,
  } as TaskInfo;
}

interface Scenario {
  id: string;
  label: string;
  note: string;
  job: TaskInfo;
}

function waitsOn(status: WaitsOnStatus): WaitsOnStatus {
  return status;
}

const SCENARIOS: readonly Scenario[] = [
  {
    id: 'open-cross-project',
    label: 'Open — cross-project (waits: CAR-3)',
    note: 'CAR-3 (Pricing-Lib) lives in another project and is still open; the card is held back from auto-pickup.',
    job: makeTask({
      references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
      waitsOn: waitsOn({
        blocked: true,
        cycleDetected: false,
        items: [
          {
            key: 'CAR-3',
            resolved: true,
            fulfilled: false,
            targetJobId: 'car-3',
            targetTitle: 'Pricing lib',
            targetState: TaskState.Ready,
            targetWatchPath: '/ws/car',
          },
        ],
      }),
    }),
  },
  {
    id: 'multiple-open',
    label: 'Multiple open (waits: AGT-2025 +1)',
    note: 'Two open dependencies; the chip names the first open one and summarises the rest with +N.',
    job: makeTask({
      key: 'WEB-UPDATE',
      title: 'Web: Preisseite aktualisieren',
      references: { dependsOn: ['AGT-2025', 'CAC-2'], relatedTo: [], blockedBy: [], supersedes: [] },
      waitsOn: waitsOn({
        blocked: true,
        cycleDetected: false,
        items: [
          {
            key: 'AGT-2025',
            resolved: true,
            fulfilled: false,
            targetJobId: 'agt-2025',
            targetTitle: 'Modell-Defaults',
            targetState: TaskState.Progress,
            targetWatchPath: '/ws/agt',
          },
          {
            key: 'CAC-2',
            resolved: true,
            fulfilled: false,
            targetJobId: 'cac-2',
            targetTitle: 'ultra-Leiter',
            targetState: TaskState.Ready,
            targetWatchPath: '/ws/cac',
          },
        ],
      }),
    }),
  },
  {
    id: 'ready',
    label: 'Fulfilled — ready (✓ CAR-3)',
    note: 'The dependency reached 6-completed; the card is now workable and picks up normally.',
    job: makeTask({
      references: { dependsOn: ['CAR-3'], relatedTo: [], blockedBy: [], supersedes: [] },
      waitsOn: waitsOn({
        blocked: false,
        cycleDetected: false,
        items: [
          {
            key: 'CAR-3',
            resolved: true,
            fulfilled: true,
            targetJobId: 'car-3',
            targetTitle: 'Pricing lib',
            targetState: TaskState.Completed,
            targetWatchPath: '/ws/car',
          },
        ],
      }),
    }),
  },
  {
    id: 'cycle',
    label: 'Cycle — config error (⚠ dep cycle)',
    note: 'A waits B waits A: can never be fulfilled. The runner reports + skips it (never deadlocks).',
    job: makeTask({
      key: 'CAR-2',
      title: 'ultra-Leiter (zyklische Abhaengigkeit)',
      references: { dependsOn: ['AGT-2025'], relatedTo: [], blockedBy: [], supersedes: [] },
      waitsOn: waitsOn({
        blocked: true,
        cycleDetected: true,
        items: [
          {
            key: 'AGT-2025',
            resolved: true,
            fulfilled: false,
            targetJobId: 'agt-2025',
            targetTitle: 'Modell-Defaults',
            targetState: TaskState.Ready,
            targetWatchPath: '/ws/agt',
          },
        ],
      }),
    }),
  },
  {
    id: 'unknown',
    label: 'Unknown — not created yet (waits: GHOST-9)',
    note: 'A not-yet-created target: allowed on write (warning), blocks pickup until it exists and completes.',
    job: makeTask({
      references: { dependsOn: ['GHOST-9'], relatedTo: [], blockedBy: [], supersedes: [] },
      waitsOn: waitsOn({
        blocked: true,
        cycleDetected: false,
        items: [
          {
            key: 'GHOST-9',
            resolved: false,
            fulfilled: false,
            targetJobId: null,
            targetTitle: null,
            targetState: null,
            targetWatchPath: null,
          },
        ],
      }),
    }),
  },
];

type Theme = 'dark' | 'light';

@Component({
  selector: 'mockup-waits-on-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskCardComponent],
  templateUrl: './waits-on-gallery.component.html',
  styleUrl: './waits-on-gallery.component.scss',
})
export class WaitsOnGalleryComponent {
  readonly theme = signal<Theme>('dark');
  readonly scenarios = SCENARIOS;

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
