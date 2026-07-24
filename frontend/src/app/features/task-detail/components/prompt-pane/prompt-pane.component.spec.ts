import { describe, expect, it } from 'vitest';
import { buildPromptTabs, nextPromptPaneTabForJobSwitch } from './prompt-pane.component';

describe('PromptPane tab state', () => {
  it('keeps Overview as the first mounted tab', () => {
    expect(nextPromptPaneTabForJobSwitch('overview', null, 'wp::task-a')).toBe('overview');
  });

  it('keeps the operator selection while the same job updates in place', () => {
    expect(nextPromptPaneTabForJobSwitch('description', 'wp::task-a', 'wp::task-a')).toBe('description');
  });

  it('resets to Overview when the underlying task changes', () => {
    expect(nextPromptPaneTabForJobSwitch('description', 'wp::task-a', 'wp::task-b')).toBe('overview');
  });

  it('does not invent per-task tab memory when navigating back to an older task', () => {
    expect(nextPromptPaneTabForJobSwitch('code-review', 'wp::task-b', 'wp::task-a')).toBe('overview');
  });
});

describe('PromptPane tab badges', () => {
  it('shows Docs count and Visual Evidence count in the tab definitions', () => {
    const tabs = buildPromptTabs(3, 2);
    expect(tabs.find(t => t.id === 'description')?.badge).toBe(3);
    expect(tabs.find(t => t.id === 'description')?.label).toBe('Docs');
    expect(tabs.find(t => t.id === 'evidence')?.badge).toBe(2);
  });

  it('does not show an Evidence badge for review evidence without screenshots', () => {
    const tabs = buildPromptTabs(3, 0);
    expect(tabs.find(t => t.id === 'evidence')?.badge).toBeNull();
  });

  it('does not show a Docs badge for a single document', () => {
    const tabs = buildPromptTabs(1, 0);
    expect(tabs.find(t => t.id === 'description')?.badge).toBeNull();
  });
});
