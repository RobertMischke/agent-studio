/**
 * Studio-Shell tab + activity-panel taxonomy (Agent Software Studio redesign).
 *
 * The new shell is a VS-Code-inspired editor surface: a left ActivityBar
 * swaps the Sidebar panel, the main pane shows tabs that map to one of
 * `StudioTabKind`, and a right Chat panel is collapsible. Each tab type
 * carries the minimum identifiers it needs; the renderer maps the kind
 * to an existing feature component (board, job-detail, etc.).
 */

/** Discrete tab kinds the editor area can host. */
export type StudioTabKind = 'board' | 'feed' | 'epics' | 'epic' | 'task' | 'hub' | 'workbench' | 'diff' | 'activity' | 'url-preview' | 'workspace-settings' | 'welcome';

/** Sidebar panel kinds reachable from the ActivityBar. */
export type StudioPanelKind = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook' | 'settings';

/** Board tab - one per project; key `board:<projectName>` or `board:__all__`. */
export interface BoardTab { kind: 'board'; projectName: string; }

/** Workspace-wide orchestrator Feed; singleton key `feed`. */
export interface FeedTab { kind: 'feed'; }

/** Epic overview tab - project-scoped or workspace-wide; key `epics:<projectName|__all__>`. */
export interface EpicsTab { kind: 'epics'; projectName: string | null; }

/** Epic detail tab - one per opened epic; key `epic:<epicKey>`. */
export interface EpicTab { kind: 'epic'; epicKey: string; viewTaskKey?: string; }

/** Task-detail tab — one per opened job; key `task:<taskKey>`. */
export interface TaskTab { kind: 'task'; taskKey: string; }

/** Deck tab. Per project; key `hub:<projectName>`. Section is the initial Deck side-nav anchor. */
export interface HubTab {
  kind: 'hub';
  projectName: string;
  section?: string;
  /** Optional exact row target when a task post-step links into Project Pipeline. */
  pipelineStepId?: string;
}

/** Isolated read-only Dossier viewer, one tab per project + Dossier id. */
export interface WorkbenchTab { kind: 'workbench'; projectName: string; workbenchId: string; title?: string; }

/** Full-screen diff tab; key `diff:<commitSha>`. */
export interface DiffTab { kind: 'diff'; commitSha: string; }

/** Full-screen activity tab; key `activity:<taskKey>`. */
export interface ActivityTab { kind: 'activity'; taskKey: string; }

/**
 * Embedded Project-URL preview tab (AGT-2067) — one per configured URL;
 * key `url-preview:<projectName>:<urlId>`. Renders the URL inside a
 * sandboxed iframe (split-view beside the Orchestrator side sheet) instead
 * of jumping to an external browser tab.
 */
export interface UrlPreviewTab { kind: 'url-preview'; projectName: string; urlId: string; }

/** Workspace settings tab — global settings surface opened from the ActivityBar gear. */
export interface WorkspaceSettingsTab { kind: 'workspace-settings'; }

/** Welcome screen — no real tab, no key. */
export interface WelcomeTab { kind: 'welcome'; }

export type StudioTab = BoardTab | FeedTab | EpicsTab | EpicTab | TaskTab | HubTab | WorkbenchTab | DiffTab | ActivityTab | UrlPreviewTab | WorkspaceSettingsTab | WelcomeTab;

/** Build the stable string key for a tab; used for selection + persistence. */
export function studioTabKey(tab: StudioTab): string {
  switch (tab.kind) {
    case 'board':    return `board:${tab.projectName}`;
    case 'feed':     return 'feed';
    case 'epics':    return `epics:${tab.projectName ?? '__all__'}`;
    case 'epic':     return `epic:${tab.epicKey}`;
    case 'task':     return `task:${tab.taskKey}`;
    case 'hub':      return tab.section === 'wiki'
      ? `hub:${tab.projectName}:wiki`
      : `hub:${tab.projectName}`;
    case 'workbench': return `workbench:${tab.projectName}:${tab.workbenchId}`;
    case 'diff':     return `diff:${tab.commitSha}`;
    case 'activity': return `activity:${tab.taskKey}`;
    case 'url-preview': return `url-preview:${tab.projectName}:${tab.urlId}`;
    case 'workspace-settings': return 'workspace-settings';
    case 'welcome':  return 'welcome';
  }
}
