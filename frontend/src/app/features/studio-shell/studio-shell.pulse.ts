/**
 * AGT-2031 — pure model + helpers for the Explorer tree's subtle auto-pickup
 * pulse indicator. Kept separate from the tree component so both the shell
 * (which derives the per-project state from the runner status) and the tree
 * (which rolls it up for collapsed nodes) read one vocabulary, and so the
 * presentational `<app-explorer-auto-pulse>` dot stays logic-free.
 *
 * - `off` — the project is not on auto (manual / paused); no pulse.
 * - `auto-idle` — auto-pickup is on but no run is live right now; a slow, quiet
 *   pulse says "this project is armed".
 * - `auto-active` — auto-pickup is on AND a run is currently executing; a
 *   livelier pulse says "something is running now".
 */
export type ProjectPulseState = 'off' | 'auto-idle' | 'auto-active';

/** Aggregated pulse for a collapsed workspace / whole tree — the strongest
 *  state of any contained project plus the names of the projects on auto (for
 *  the "which ones" tooltip). */
export interface ExplorerPulseAggregate {
  state: ProjectPulseState;
  autoProjects: readonly string[];
}

/** Hover help for a single project-row pulse dot; empty when there is no dot. */
export function pulseTooltip(state: ProjectPulseState): string {
  switch (state) {
    case 'auto-active': return 'Auto-pickup is on — a run is in progress';
    case 'auto-idle':   return 'Auto-pickup is on — waiting for the next task';
    default:            return '';
  }
}

/** Accessible label mirroring {@link pulseTooltip} without the dash prose. */
export function pulseAriaLabel(state: ProjectPulseState): string {
  switch (state) {
    case 'auto-active': return 'Auto-pickup running';
    case 'auto-idle':   return 'Auto-pickup on';
    default:            return '';
  }
}

/**
 * Roll a set of project names up into one aggregate: the strongest child state
 * wins (active > idle > off) and the names of every project on auto ride along
 * (sorted) for the "which ones" tooltip.
 */
export function aggregatePulse(
  names: readonly string[],
  pulses: ReadonlyMap<string, ProjectPulseState>,
): ExplorerPulseAggregate {
  let anyIdle = false;
  let anyActive = false;
  const autoProjects: string[] = [];
  for (const name of names) {
    const state = pulses.get(name) ?? 'off';
    if (state === 'off') continue;
    autoProjects.push(name);
    if (state === 'auto-active') anyActive = true;
    else anyIdle = true;
  }
  autoProjects.sort((a, b) => a.localeCompare(b));
  const state: ProjectPulseState = anyActive ? 'auto-active' : anyIdle ? 'auto-idle' : 'off';
  return { state, autoProjects };
}

/** Hover help for an aggregate pulse: lists the projects currently on auto. */
export function aggregatePulseTooltip(agg: ExplorerPulseAggregate): string {
  if (agg.state === 'off') return '';
  const lead = agg.state === 'auto-active' ? 'Auto-pickup running' : 'Auto-pickup on';
  return `${lead}: ${agg.autoProjects.join(', ')}`;
}

/**
 * Derive the per-project pulse map from the runner status: a project is
 * `auto-idle` when its runner mode is an auto mode (`auto-continuous` /
 * `auto-single`) with no live run, and `auto-active` when an auto project also
 * has a live run (`activeJobId` set). Manual / paused projects are omitted
 * (treated as `off`). Only rows the shell already renders are considered.
 */
export function deriveProjectPulseByName(
  projects: Readonly<Record<string, { mode?: string; activeJobId?: string | null } | undefined>>,
  rows: readonly { name: string }[],
): ReadonlyMap<string, ProjectPulseState> {
  const map = new Map<string, ProjectPulseState>();
  for (const row of rows) {
    const status = projects[row.name];
    const mode = status?.mode ?? 'manual';
    if (mode !== 'auto-continuous' && mode !== 'auto-single') continue;
    map.set(row.name, status?.activeJobId ? 'auto-active' : 'auto-idle');
  }
  return map;
}
