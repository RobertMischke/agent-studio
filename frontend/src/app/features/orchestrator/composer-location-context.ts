import type { TaskInfo } from '../../models/task.model';
import { projectRailLabel } from '../project-detail';
import type { StudioTab } from '../studio-shell';

/**
 * Presentational location context the host feeds into the composer's
 * standard footer: the large scope (project), the active surface, and an
 * optional identity detail (task key, URL id, commit…).
 *
 * The side sheet maps this host-owned location shape to the chat library's
 * `composerContext` input. Keeping the shell contract separate preserves
 * nullable navigation state at the application boundary.
 */
export interface ComposerLocationContext {
  project: string | null;
  surface: string;
  detail?: string;
  /** Task identity carried by the same active-tab projection the footer shows. */
  taskKey?: string;
  taskId?: string;
  taskTitle?: string;
  taskState?: string;
  taskWatchPath?: string;
  /** Dossier identity carried by a repository Dossier tab. */
  dossierId?: string;
  dossierTitle?: string;
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
  const taskKey = task?.displayKey ?? task?.key ?? task?.taskKey ?? tabKey;
  return {
    project: task?.projectName ?? null,
    surface,
    detail: taskKey,
    taskKey,
    taskId: task?.id ?? tabKey,
    ...(task?.title ? { taskTitle: task.title } : {}),
    ...(task?.state ? { taskState: task.state } : {}),
    ...(task?.watchPath ? { taskWatchPath: task.watchPath } : {}),
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
    case 'feed':
      return { project: null, surface: 'Feed' };
    case 'chat-history':
      return { project: null, surface: 'Chat History' };
    case 'task':
      return taskContext('Task', tab.taskKey, tasks);
    case 'workbench':
      return {
        project: tab.projectName,
        surface: 'Dossier',
        detail: tab.title ?? tab.workbenchId,
        dossierId: tab.workbenchId,
        dossierTitle: tab.title ?? tab.workbenchId,
      };
    case 'workbenches':
      return { project: tab.projectName, surface: 'Dossiers' };
    case 'activity':
      return taskContext('Activity', tab.taskKey, tasks);
    case 'epic':
      return taskContext('Epic', tab.epicKey, tasks);
    case 'epics':
      return { project: tab.projectName, surface: 'Epics' };
    case 'hub':
      return {
        project: tab.projectName,
        surface: tab.section ? projectRailLabel(tab.section) : 'Deck',
      };
    case 'url-preview':
      return { project: tab.projectName, surface: 'URL preview', detail: tab.urlId };
    case 'diff':
      return { project: null, surface: 'Diff', detail: tab.commitSha.slice(0, 8) };
    case 'workspace-settings':
      return { project: null, surface: 'Workspace Settings' };
    case 'welcome':
      return { project: null, surface: 'Welcome' };
  }
}
