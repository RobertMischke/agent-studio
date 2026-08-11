import { describe, expect, it } from 'vitest';
import { taskDetailShortcutTargetAllowed, taskNavigationOwnsFocus } from './task-detail-keyboard.util';

function keydown(target: HTMLElement, key = 'ArrowDown'): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe('task detail keyboard ownership', () => {
  it('gives arrow paging to task navigation surfaces', () => {
    const taskNav = document.createElement('aside');
    taskNav.className = 'task-nav';
    const task = document.createElement('button');
    taskNav.appendChild(task);

    expect(taskNavigationOwnsFocus(keydown(task))).toBe(true);
  });

  it('does not give arrow paging to detail content', () => {
    const detail = document.createElement('main');
    detail.dataset['testid'] = 'studio-task';

    expect(taskNavigationOwnsFocus(keydown(detail))).toBe(false);
  });

  it('keeps editable targets and modified shortcuts excluded', () => {
    const input = document.createElement('input');
    expect(taskDetailShortcutTargetAllowed(keydown(input))).toBe(false);

    const button = document.createElement('button');
    const modified = new KeyboardEvent('keydown', { key: 'ArrowDown', ctrlKey: true });
    button.dispatchEvent(modified);
    expect(taskDetailShortcutTargetAllowed(modified)).toBe(false);
  });
});
