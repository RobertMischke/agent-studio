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
export type StudioTabKind = 'board' | 'backlog' | 'epics' | 'task' | 'hub' | 'diff' | 'activity' | 'welcome';

/** Sidebar panel kinds reachable from the ActivityBar. */
export type StudioPanelKind = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook' | 'settings';

/** Board tab - one per project; key `board:<projectName>` or `board:__all__`. */
export interface BoardTab { kind: 'board'; projectName: string; sticky?: boolean; }

/** Backlog triage tab - project-scoped or workspace-wide; key `backlog:<projectName|__all__>`. */
export interface BacklogTab { kind: 'backlog'; projectName: string | null; }

/** Epic overview tab - project-scoped or workspace-wide; key `epics:<projectName|__all__>`. */
export interface EpicsTab { kind: 'epics'; projectName: string | null; }

/** Task-detail tab — one per opened job; key `task:<taskKey>`. */
export interface TaskTab { kind: 'task'; taskKey: string; }

/** Project Hub tab — per project; key `hub:<projectName>`. Section is the initial Hub side-nav anchor. */
export interface HubTab { kind: 'hub'; projectName: string; section?: string; }

/** Full-screen diff tab; key `diff:<commitSha>`. */
export interface DiffTab { kind: 'diff'; commitSha: string; }

/** Full-screen activity tab; key `activity:<taskKey>`. */
export interface ActivityTab { kind: 'activity'; taskKey: string; }

/** Welcome screen — no real tab, no key. */
export interface WelcomeTab { kind: 'welcome'; }

export type StudioTab = BoardTab | BacklogTab | EpicsTab | TaskTab | HubTab | DiffTab | ActivityTab | WelcomeTab;

/** Build the stable string key for a tab; used for selection + persistence. */
export function studioTabKey(tab: StudioTab): string {
  switch (tab.kind) {
    case 'board':    return `board:${tab.projectName}`;
    case 'backlog':  return `backlog:${tab.projectName ?? '__all__'}`;
    case 'epics':    return `epics:${tab.projectName ?? '__all__'}`;
    case 'task':     return `task:${tab.taskKey}`;
    case 'hub':      return `hub:${tab.projectName}`;
    case 'diff':     return `diff:${tab.commitSha}`;
    case 'activity': return `activity:${tab.taskKey}`;
    case 'welcome':  return 'welcome';
  }
}
