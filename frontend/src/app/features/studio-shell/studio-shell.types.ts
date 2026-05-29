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
export type StudioTabKind = 'board' | 'task' | 'hub' | 'diff' | 'activity' | 'welcome';

/** Sidebar panel kinds reachable from the ActivityBar. */
export type StudioPanelKind = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook' | 'settings';

/**
 * Board tab — pinned per project; key `board:<projectName>` or `board:__all__`.
 *
 * `sticky` marks the default Board tab that the shell always keeps mounted
 * so the user can never get stranded in a "no tabs open" limbo. The tab
 * cannot be closed (close-X is hidden in the UI; service `close*` ops
 * preserve it), and survives reloads via the same persistence path.
 */
export interface BoardTab { kind: 'board'; projectName: string; sticky?: boolean; }

/** Task-detail tab — one per opened job; key `task:<jobKey>`. */
export interface TaskTab { kind: 'task'; jobKey: string; }

/** Project Hub tab — per project; key `hub:<projectName>`. Section is the initial Hub side-nav anchor. */
export interface HubTab { kind: 'hub'; projectName: string; section?: string; }

/** Full-screen diff tab; key `diff:<commitSha>`. */
export interface DiffTab { kind: 'diff'; commitSha: string; }

/** Full-screen activity tab; key `activity:<jobKey>`. */
export interface ActivityTab { kind: 'activity'; jobKey: string; }

/** Welcome screen — no real tab, no key. */
export interface WelcomeTab { kind: 'welcome'; }

export type StudioTab = BoardTab | TaskTab | HubTab | DiffTab | ActivityTab | WelcomeTab;

/** Build the stable string key for a tab; used for selection + persistence. */
export function studioTabKey(tab: StudioTab): string {
  switch (tab.kind) {
    case 'board':    return `board:${tab.projectName}`;
    case 'task':     return `task:${tab.jobKey}`;
    case 'hub':      return `hub:${tab.projectName}`;
    case 'diff':     return `diff:${tab.commitSha}`;
    case 'activity': return `activity:${tab.jobKey}`;
    case 'welcome':  return 'welcome';
  }
}
