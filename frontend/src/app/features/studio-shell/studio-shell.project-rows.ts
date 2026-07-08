import type { GroupedJobs, TaskInfo } from '../../models/task.model';
import { projectIdentity } from '../../services/project-identity.util';
import type { StructuredTooltip } from 'coding-agent-chat/shared';

export interface ExplorerLaneCounts {
  ready: number;
  progress: number;
  humanReview: number;
}

/**
 * Hover help for the three Explorer board lane counters (grey Ready /
 * orange In Progress / green Human Review). Each entry feeds the canonical
 * `[cacTooltip]` directive as a {@link StructuredTooltip} so the number says
 * which lane it counts and what that lane means. Prose mirrors the lane
 * concept docs (`docs/in-app-help/lane-guides/lane-{2-ready,3-progress,5-human-review}.md`).
 */
export const BOARD_LANE_COUNT_TOOLTIPS: Record<keyof ExplorerLaneCounts, StructuredTooltip> = {
  ready: {
    title: 'Ready',
    body: 'Refined tasks queued for a coding agent. The orchestrator runs the top card next when a slot frees up.',
  },
  progress: {
    title: 'In Progress',
    body: 'Tasks the orchestrator is actively running now, or resuming between attempts. One per project at a time.',
  },
  humanReview: {
    title: 'Human Review',
    body: 'Finished runs waiting for your review. Accept the work or send it back for another pass.',
  },
};

export interface ProjectSidebarRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  laneCounts: ExplorerLaneCounts;
  isActive: boolean;
}

interface ProjectAccumulator {
  totalJobs: number;
  laneCounts: ExplorerLaneCounts;
}

const EMPTY_LANE_COUNTS: ExplorerLaneCounts = { ready: 0, progress: 0, humanReview: 0 };

function emptyLaneCounts(): ExplorerLaneCounts {
  return { ...EMPTY_LANE_COUNTS };
}

export function laneCountsFor(project: { laneCounts?: ExplorerLaneCounts }): ExplorerLaneCounts {
  return project.laneCounts ?? EMPTY_LANE_COUNTS;
}

export function boardLaneCountsLabel(project: { laneCounts?: ExplorerLaneCounts }): string {
  const counts = laneCountsFor(project);
  return `${counts.ready} ready, ${counts.progress} in progress, ${counts.humanReview} human review`;
}

export function buildProjectSidebarRows(
  grouped: GroupedJobs,
  knownProjectNames: readonly string[],
  active: string | null,
): ProjectSidebarRow[] {
  const projects = new Map<string, ProjectAccumulator>();
  const ensureProject = (name: string): ProjectAccumulator => {
    let value = projects.get(name);
    if (!value) {
      value = { totalJobs: 0, laneCounts: emptyLaneCounts() };
      projects.set(name, value);
    }
    return value;
  };

  for (const [laneKey, lane] of Object.entries(grouped)) {
    if (laneKey === 'archive') continue;
    for (const job of lane as TaskInfo[]) {
      const project = ensureProject(job.projectName ?? '');
      project.totalJobs++;
      if (laneKey === 'ready') project.laneCounts.ready++;
      else if (laneKey === 'progress') project.laneCounts.progress++;
      else if (laneKey === 'humanReview') project.laneCounts.humanReview++;
    }
  }

  for (const name of knownProjectNames) ensureProject(name);

  return Array.from(projects.entries())
    .map(([name, project]) => {
      const id = projectIdentity(name);
      return {
        name,
        initial: id.initial,
        color: id.color,
        totalJobs: project.totalJobs,
        laneCounts: project.laneCounts,
        isActive: active === name,
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));
}
