import type { TaskInfo } from '../../models/task.model';
import { projectRailLabel } from '../project-detail';
import type { StudioTab } from '../studio-shell';

/**
 * Presentational "where is the operator right now" context for the
 * orchestrator composer. The chat library currently exposes a plain
 * `contextLabel` string on `cac-chat` (no structured context input), so the
 * host keeps the structured value and renders it via
 * {@link composerLocationLabel}.
 */
export interface ComposerLocationContext {
  project: string | null;
  surface: string;
  detail?: string;
}

/** Canonical `contextLabel` rendering: `surface` or `surface · detail`. */
export function composerLocationLabel(context: ComposerLocationContext | null): string | null {
  if (!context) return null;
  return context.detail ? `${context.surface} · ${context.detail}` : context.surface;
}

function taskFor(tabKey: string, tasks: readonly TaskInfo[]): TaskInfo | undefined {
  return tasks.find(task => task.taskKey === tabKey || task.displayKey === tabKey || task.key === tabKey);
}

function taskContext(surface: string, tabKey: string, tasks: readonly TaskInfo[]): ComposerLocationContext {
  const task = taskFor(tabKey, tasks);
  return {
    project: task?.projectName ?? null,
    surface,
    detail: task?.displayKey ?? task?.key ?? task?.taskKey ?? tabKey,
  };
}

/**
 * Project the canonical active Studio tab into CAC's presentational context
 * contract. The composer receives this value and does not inspect tabs or
 * re-derive navigation state itself.
 */
export function buildComposerLocationContext(
  tab: StudioTab | null,
  tasks: readonly TaskInfo[],
): ComposerLocationContext | null {
  if (!tab) return null;

  switch (tab.kind) {
    case 'board':
      return { project: tab.projectName === '__all__' ? null : tab.projectName, surface: 'Board' };
    case 'task':
      return taskContext('Task', tab.taskKey, tasks);
    case 'activity':
      return taskContext('Activity', tab.taskKey, tasks);
    case 'epic':
      return taskContext('Epic', tab.epicKey, tasks);
    case 'epics':
      return { project: tab.projectName, surface: 'Epics' };
    case 'hub':
      return {
        project: tab.projectName,
        surface: tab.section ? projectRailLabel(tab.section) : 'Project Hub',
      };
    case 'url-preview':
      return { project: tab.projectName, surface: 'URL preview', detail: tab.urlId };
    case 'workbench':
      return { project: tab.projectName, surface: 'Workbench', detail: tab.title ?? tab.workbenchId };
    case 'diff':
      return { project: null, surface: 'Diff', detail: tab.commitSha.slice(0, 8) };
    case 'workspace-settings':
      return { project: null, surface: 'Workspace Settings' };
    case 'welcome':
      return { project: null, surface: 'Welcome' };
  }
}
