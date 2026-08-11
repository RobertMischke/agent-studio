import type { TaskInfo } from '../../models/task.model';
import { projectRailLabel } from '../project-detail';
import type { StudioTab } from '../studio-shell';

/**
 * Host-owned location context used to resolve the chat scope, automatic
 * evidence, and the Add context picker's current-surface suggestion. It is
 * deliberately not repeated as persistent composer chrome.
 */
export interface ComposerLocationContext {
  project: string | null;
  surface: string;
  detail?: string;
  /** Task identity carried by the active-tab projection. */
  taskKey?: string;
  taskId?: string;
  taskTitle?: string;
  taskState?: string;
  taskWatchPath?: string;
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
 * Project the canonical active Studio tab into host-owned chat context. The
 * side sheet uses this value without inspecting tabs or re-deriving navigation
 * state itself.
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
      return { project: tab.projectName, surface: 'Dossier', detail: tab.title ?? tab.workbenchId };
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
