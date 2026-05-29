import { describe, expect, it } from 'vitest';
import { deriveProtocolVerdict, scanForBlockers, type ProtocolVerdictInputs } from './protocol-verdict';

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

  // Regression: status.md said Result: Success but the Notes section described a
  // sandbox-denied blocker. Verdict was rendered as green Success; user wanted
  // red Problem because the task was not actually fulfilled.
  describe('Result: Success body-blocker downgrade', () => {
    it('downgrades Result: Success to Blocked when Notes contains "was blocked"', () => {
      const md = [
        '# Status',
        '',
        '- Result: Success',
        '- Duration: 4 min',
        '',
        '## What Was Done',
        '- Attempted NuGet restore',
        '',
        '## Notes',
        '- Implementation was blocked by `obj/*.tmp` access denied during NuGet restore.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('problem');
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('Notes');
      expect(v.detail.toLowerCase()).toContain('blocked');
    });

    it('downgrades on "access denied" in Open Items', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Open Items',
        '- Sandbox access denied; needs external verification.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('problem');
      expect(v.detail).toContain('Open Items');
    });

    it('downgrades on "konnte nicht" (German) in Notes', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Notes',
        '- Build konnte nicht abgeschlossen werden.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('problem');
    });

    it('downgrades on "requires external" in What Was Done', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## What Was Done',
        '- Edit applied locally; requires external verification before merging.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('problem');
    });

    it('keeps Result: Success when blocker phrase only appears in unrelated sections', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Images',
        '- ![](results/blocked-icon.png)',
        '',
        '## What Was Done',
        '- All tests passed.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
      expect(v.label).toBe('Success');
    });

    it('does not downgrade Result: NoOp or other non-success results', () => {
      const md = [
        '# Status',
        '- Result: NoOp',
        '',
        '## Notes',
        '- Was blocked earlier but recovered.'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
    });

    it('lets a [[TASK_DONE]] sentinel beat the body-blocker downgrade', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## Notes',
        '- Was briefly blocked but recovered.',
        '',
        '[[TASK_DONE]]'
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('ok');
    });
  });

  describe('scanForBlockers', () => {
    it('returns the matched phrase, sentence, and section heading', () => {
      const md = [
        '## Notes',
        '- Implementation was blocked by sandbox.'
      ].join('\n');
      const hit = scanForBlockers(md);
      expect(hit).not.toBeNull();
      expect(hit?.phrase).toBe('blocked');
      expect(hit?.section).toBe('Notes');
      expect(hit?.sentence.toLowerCase()).toContain('blocked by sandbox');
    });

    it('ignores phrase hits in non-blocker sections', () => {
      const md = [
        '## Images',
        '- access denied note in caption'
      ].join('\n');
      expect(scanForBlockers(md)).toBeNull();
    });

    it('returns null for empty/null input', () => {
      expect(scanForBlockers(null)).toBeNull();
      expect(scanForBlockers('')).toBeNull();
      expect(scanForBlockers('# Status\n- Result: Success')).toBeNull();
    });
  });
});
