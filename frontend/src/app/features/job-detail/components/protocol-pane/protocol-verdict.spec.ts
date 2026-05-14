import { describe, expect, it } from 'vitest';
import { deriveProtocolVerdict, type ProtocolVerdictInputs } from './protocol-verdict';

function baseInputs(overrides: Partial<ProtocolVerdictInputs> = {}): ProtocolVerdictInputs {
  return {
    isRunning: false,
    summaryStatus: 'ready',
    statusMarkdown: null,
    outcomeIssue: null,
    hasActivity: true,
    ...overrides
  };
}

describe('deriveProtocolVerdict', () => {
  it('is unclear (running) when isRunning beats every other signal', () => {
    const v = deriveProtocolVerdict(baseInputs({
      isRunning: true,
      statusMarkdown: '# Status\n- Result: Success\n[[TASK_DONE]]'
    }));
    expect(v.kind).toBe('unclear');
    expect(v.emoji).toBe('🟡');
    expect(v.label).toBe('Running');
  });

  it('flags problem when summary generation itself failed', () => {
    const v = deriveProtocolVerdict(baseInputs({ summaryStatus: 'failed', statusMarkdown: null }));
    expect(v.kind).toBe('problem');
    expect(v.emoji).toBe('🔴');
    expect(v.label).toBe('Summary failed');
  });

  it('flags problem on a high-severity outcome issue', () => {
    const v = deriveProtocolVerdict(baseInputs({
      outcomeIssue: { kind: 'watchdog-timeout', label: 'Watchdog', severity: 'High', summary: '120s silence', lastSeenAt: null }
    }));
    expect(v.kind).toBe('problem');
    expect(v.label).toBe('Watchdog');
  });

  it('reads [[TASK_DONE]] as ok', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: 'done\n\n[[TASK_DONE]]\n' }));
    expect(v.kind).toBe('ok');
    expect(v.label).toBe('Done');
  });

  it('reads [[TASK_BLOCKED:reason]] as problem with the reason', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: '[[TASK_BLOCKED:missing API key]]' }));
    expect(v.kind).toBe('problem');
    expect(v.label).toBe('Blocked');
    expect(v.detail).toContain('missing API key');
  });

  it('reads [[TASK_NEEDS_INPUT]] as unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: 'q\n[[TASK_NEEDS_INPUT:which env?]]' }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Needs input');
  });

  it('reads [[TASK_NOOP]] as ok', () => {
    const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: '[[TASK_NOOP]]' }));
    expect(v.kind).toBe('ok');
  });

  it('uses last sentinel when multiple are present', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '[[TASK_BLOCKED:earlier]]\n\n[[TASK_DONE]]'
    }));
    expect(v.kind).toBe('ok');
  });

  it('falls back to the Result: line when no sentinel is present', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n\n- Result: Success\n- Duration: 4 min\n'
    }));
    expect(v.kind).toBe('ok');
    expect(v.label).toBe('Success');
  });

  it('maps Result: Failed -> problem', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Failed\n'
    }));
    expect(v.kind).toBe('problem');
  });

  it('maps Result: Partial -> unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Partial\n'
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Partial');
  });

  it('maps Result: NeedsInput -> unclear', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: NeedsInput\n'
    }));
    expect(v.kind).toBe('unclear');
  });

  it('uses warn-severity outcome issue when no sentinel/result is available', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: null,
      outcomeIssue: { kind: 'classifier-unknown', label: 'Unknown reply', severity: 'Warn', summary: '', lastSeenAt: null }
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Unknown reply');
  });

  it('is unclear when there is activity but no signal lands', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n\n## What Was Done\n- did stuff\n',
      hasActivity: true
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('Unclear');
  });

  it('is unclear (no run yet) when no signal and no activity', () => {
    const v = deriveProtocolVerdict(baseInputs({
      summaryStatus: 'none',
      statusMarkdown: null,
      hasActivity: false
    }));
    expect(v.kind).toBe('unclear');
    expect(v.label).toBe('No run yet');
  });

  it('treats sentinel as authoritative over Result line', () => {
    const v = deriveProtocolVerdict(baseInputs({
      statusMarkdown: '# Status\n- Result: Failed\n\n[[TASK_DONE]]'
    }));
    expect(v.kind).toBe('ok');
  });
});
