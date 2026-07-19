import { describe, expect, it } from 'vitest';
import { buildVisibleCliTaskPrompt } from './visible-cli-task.service';

describe('buildVisibleCliTaskPrompt', () => {
  it('keeps scope, exact input, duration, command, and context in task history', () => {
    const prompt = buildVisibleCliTaskPrompt({
      title: 'Probe host',
      scope: 'Remote host probe',
      reason: 'Confirm the runner can accept work.',
      command: 'agent-runner --health-check',
      prompt: 'Run the health check and explain failures.',
      expectedDuration: '2 to 4 minutes',
      context: { host: 'runner-02' },
    });

    expect(prompt).toContain('# Remote host probe');
    expect(prompt).toContain('Run the health check and explain failures.');
    expect(prompt).toContain('`agent-runner --health-check`');
    expect(prompt).toContain('2 to 4 minutes');
    expect(prompt).toContain('- host: runner-02');
    expect(prompt).toContain('Keep all progress and output in this task conversation.');
  });
});
