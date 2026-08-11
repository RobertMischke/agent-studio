const TASK_NAVIGATION_SELECTOR =
  '.task-nav, [data-testid="studio-board"], [data-testid="kanban-dashboard"]';

export function taskDetailShortcutTargetAllowed(event: KeyboardEvent): boolean {
  if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey) return false;
  const target = event.target;
  if (!(target instanceof HTMLElement)) return true;
  if (target.isContentEditable) return false;
  return target.tagName !== 'INPUT' && target.tagName !== 'TEXTAREA' && target.tagName !== 'SELECT';
}

export function taskNavigationOwnsFocus(event: KeyboardEvent): boolean {
  const target = event.target;
  return target instanceof Element && target.closest(TASK_NAVIGATION_SELECTOR) !== null;
}
