import { taskDetailShortcutTargetAllowed, taskNavigationOwnsFocus } from './task-detail-keyboard.util';

function keydown(target: HTMLElement, key = 'ArrowDown'): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe('task detail keyboard ownership', () => {
  it('allows arrow paging only when task navigation owns focus', () => {
    const navigation = document.createElement('aside');
    navigation.className = 'task-nav';
    const task = document.createElement('button');
    navigation.append(task);
    const panelControl = document.createElement('button');

    expect(taskNavigationOwnsFocus(keydown(task))).toBe(true);
    expect(taskNavigationOwnsFocus(keydown(panelControl))).toBe(false);
  });

  it('keeps typing targets out of global task shortcuts', () => {
    const input = document.createElement('input');
    const button = document.createElement('button');

    expect(taskDetailShortcutTargetAllowed(keydown(input))).toBe(false);
    expect(taskDetailShortcutTargetAllowed(keydown(button))).toBe(true);
  });
});
