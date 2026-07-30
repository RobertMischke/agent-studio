import { describe, expect, it } from 'vitest';
import { buildInspectorTabs } from './protocol-pane-view-model';

function resultTab(overrides: Partial<Parameters<typeof buildInspectorTabs>[0]> = {}) {
  return buildInspectorTabs({
    summaryStatus: 'none',
    hasStatusMarkdown: false,
    hasCliActivity: false,
    isHumanReview: false,
    isRunning: false,
    ...overrides,
  }).find(tab => tab.id === 'protocol')!;
}

describe('buildInspectorTabs', () => {
  it('keeps the fixed Task, Activity, Result order', () => {
    expect(buildInspectorTabs({
      summaryStatus: 'none',
      hasStatusMarkdown: false,
      hasCliActivity: false,
      isHumanReview: false,
      isRunning: false,
    }).map(tab => tab.label)).toEqual(['Task', 'Activity', 'Result']);
  });

  it('keeps Result disabled for a fresh task with no run activity', () => {
    expect(resultTab().disabled).toBe(true);
  });

  it('enables Result when CLI activity exists without a summary or verdict', () => {
    expect(resultTab({ hasCliActivity: true }).disabled).toBe(false);
  });

  it('keeps Result available in human review even when the summary is missing', () => {
    expect(resultTab({ isHumanReview: true }).disabled).toBe(false);
  });
});
