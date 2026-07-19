import { describe, expect, it } from 'vitest';
import {
  deriveProtocolVerdict,
  isAcceptedStand,
  parseDuration,
  scanForBlockers,
  stripStatusHeader,
  type ProtocolVerdictInputs,
} from './protocol-verdict';

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

  describe('duration carried on the verdict pill', () => {
    it('parses "- Duration: 4 min" from a typical # Status header', () => {
      const md = ['# Status', '', '- Result: Success', '- Duration: 4 min', '', '## Notes', '- ok'].join('\n');
      expect(parseDuration(md)).toBe('4 min');
    });

    it('parses Duration even when nested under ## Status (legacy authoring)', () => {
      const md = ['## Status', '- Result: Failed', '- Duration: 12s'].join('\n');
      expect(parseDuration(md)).toBe('12s');
    });

    it('returns null when no Duration line is present', () => {
      expect(parseDuration('# Status\n- Result: Success')).toBeNull();
      expect(parseDuration(null)).toBeNull();
      expect(parseDuration('')).toBeNull();
    });

    it('attaches duration to the resolved verdict', () => {
      const v = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n\n- Result: Success\n- Duration: 4 min\n'
      }));
      expect(v.duration).toBe('4 min');
      expect(v.kind).toBe('ok');
    });

    it('verdict.duration is null when status.md omits Duration', () => {
      const v = deriveProtocolVerdict(baseInputs({
        statusMarkdown: '# Status\n\n- Result: Success\n'
      }));
      expect(v.duration).toBeNull();
    });

    it('verdict.duration is null when no markdown exists yet', () => {
      const v = deriveProtocolVerdict(baseInputs({ summaryStatus: 'none', statusMarkdown: null, hasActivity: false }));
      expect(v.duration).toBeNull();
    });
  });

  describe('stripStatusHeader', () => {
    it('removes the # Status section + Result/Duration list before the next heading', () => {
      const md = [
        '# Status',
        '',
        '- Result: Success',
        '- Duration: 4 min',
        '',
        '## What Was Done',
        '- did stuff',
        '',
        '## Notes',
        '- a note'
      ].join('\n');
      const out = stripStatusHeader(md);
      expect(out).not.toContain('# Status');
      expect(out).not.toContain('Duration:');
      expect(out).not.toMatch(/Result:\s*Success/);
      expect(out).toMatch(/^## What Was Done/);
      expect(out).toContain('did stuff');
      expect(out).toContain('## Notes');
    });

    it('also strips ## Status (lower heading level)', () => {
      const md = ['## Status', '- Result: Failed', '- Duration: 2 min', '', '## Notes', '- broke'].join('\n');
      const out = stripStatusHeader(md);
      expect(out).not.toContain('Status');
      expect(out).toMatch(/^## Notes/);
    });

    it('leaves status.md untouched when there is no Status heading', () => {
      const md = '## Other\n- nope';
      expect(stripStatusHeader(md)).toBe(md);
    });

    it('returns empty string for null/empty input', () => {
      expect(stripStatusHeader(null)).toBe('');
      expect(stripStatusHeader('')).toBe('');
    });

    it('does not strip a heading that merely contains the word Status', () => {
      const md = ['## Status summary (the agent\'s own report)', '- detail'].join('\n');
      const out = stripStatusHeader(md);
      expect(out).toBe(md);
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

  // BEFUND 1: the reason was cut mid-word ("…ts' rendering 5 canonical
  // states…") because the sentence scanner treated the dot in a file
  // extension / decimal as a sentence boundary, and the sentinel reason
  // regex stopped at the first special character.
  describe('robust blocker reason parsing (BEFUND 1)', () => {
    it('does not cut the reason at a file-extension dot', () => {
      const md = [
        '# Status',
        '- Result: Success',
        '',
        '## What Was Done',
        '- Rewrote `protocol-verdict.ts` rendering 5 canonical states, but could not verify the banner.',
      ].join('\n');
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: md }));
      expect(v.kind).toBe('problem');
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('Rewrote');
      expect(v.detail).toContain('could not verify the banner');
      // The old bug surfaced the tail "ts' rendering 5 canonical states…".
      expect(v.detail).not.toMatch(/:\s*ts['`]/);
    });

    it('keeps decimals and extensions intact in scanForBlockers', () => {
      const hit = scanForBlockers('## Notes\n- Upgrade to 5.1 was blocked by config.sys lock.');
      expect(hit?.sentence).toContain('Upgrade to 5.1 was blocked by config.sys lock');
    });

    it('keeps a single ] inside the TASK_BLOCKED sentinel reason', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_BLOCKED:cannot parse arr[0] without schema]]' }),
      );
      expect(v.label).toBe('Blocked');
      expect(v.detail).toContain('cannot parse arr[0] without schema');
    });

    it('keeps quotes and colons in the TASK_BLOCKED sentinel reason', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_BLOCKED:missing "API key": see docs]]' }),
      );
      expect(v.detail).toContain('missing "API key": see docs');
    });
  });

  // BEFUND 2: one precedence rule — the current lane / review decision leads
  // the head verdict; a Blocked from a superseded run is demoted to collapsed
  // history and must never be the head banner after an accepted stand.
  describe('leading-state precedence demotes a superseded blocker (BEFUND 2)', () => {
    const blockedMd = '[[TASK_BLOCKED:sandbox denied write to /etc]]';

    it('leads with Blocked when no accepted stand is present', () => {
      const v = deriveProtocolVerdict(baseInputs({ statusMarkdown: blockedMd }));
      expect(v.kind).toBe('problem');
      expect(v.label).toBe('Blocked');
      expect(v.superseded).toBeNull();
    });

    it('demotes Blocked to history when orchestratorVerdict is accept', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: blockedMd, orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('ok');
      expect(v.label).toBe('Accepted');
      expect(v.superseded).not.toBeNull();
      expect(v.superseded?.label).toBe('Blocked');
      expect(v.superseded?.detail).toContain('sandbox denied write to /etc');
    });

    it('demotes Blocked to history when the card lives in 6-completed', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: blockedMd, laneState: '6-completed' }),
      );
      expect(v.kind).toBe('ok');
      expect(v.superseded?.label).toBe('Blocked');
    });

    it('demotes a Result: Failed run outcome as well', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '# Status\n- Result: Failed\n', orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('ok');
      expect(v.superseded?.label).toBe('Failed');
    });

    it('does NOT demote under a reissue / escalate / pending decision', () => {
      for (const decision of ['reissue', 'escalate', 'pending'] as const) {
        const v = deriveProtocolVerdict(
          baseInputs({ statusMarkdown: blockedMd, orchestratorVerdict: decision }),
        );
        expect(v.kind, decision).toBe('problem');
        expect(v.superseded, decision).toBeNull();
      }
    });

    it('does NOT demote a summary-failed problem (orthogonal to acceptance)', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ summaryStatus: 'failed', statusMarkdown: null, orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('problem');
      expect(v.label).toBe('Summary failed');
      expect(v.superseded).toBeNull();
    });

    it('leaves an OK verdict untouched under an accepted stand', () => {
      const v = deriveProtocolVerdict(
        baseInputs({ statusMarkdown: '[[TASK_DONE]]', orchestratorVerdict: 'accept' }),
      );
      expect(v.kind).toBe('ok');
      expect(v.label).toBe('Done');
      expect(v.superseded).toBeNull();
    });
  });

  describe('isAcceptedStand', () => {
    it('is true for an accept verdict and completed / archive lanes', () => {
      expect(isAcceptedStand(baseInputs({ orchestratorVerdict: 'accept' }))).toBe(true);
      expect(isAcceptedStand(baseInputs({ laneState: '6-completed' }))).toBe(true);
      expect(isAcceptedStand(baseInputs({ laneState: '7-archive' }))).toBe(true);
    });

    it('is false for in-flight lanes and non-accept decisions', () => {
      expect(isAcceptedStand(baseInputs())).toBe(false);
      expect(isAcceptedStand(baseInputs({ laneState: '4-auto-review' }))).toBe(false);
      expect(isAcceptedStand(baseInputs({ orchestratorVerdict: 'reissue' }))).toBe(false);
    });
  });
});
