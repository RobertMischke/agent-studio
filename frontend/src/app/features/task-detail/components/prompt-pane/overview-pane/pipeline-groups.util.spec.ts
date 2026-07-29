import { describe, expect, it } from 'vitest';
import {
  buildPipelineGroups,
  groupAriaLabel,
  groupTone,
  groupToneLabel,
  pipelineComplete,
  pipelineStarted,
  rowHasConcern,
  rowIsRisk,
  type PipelineGroupRowLike,
} from './pipeline-groups.util';

function row(partial: Partial<PipelineGroupRowLike> & { phaseKey: string }): PipelineGroupRowLike {
  return {
    phaseLabel: partial.phaseKey.toUpperCase(),
    phaseDescription: `${partial.phaseKey} phase`,
    status: 'pending',
    verdict: null,
    totalTokens: 0,
    ...partial,
  };
}

describe('groupTone', () => {
  it('is danger when any step failed', () => {
    expect(groupTone([row({ phaseKey: 'core', status: 'failed' }), row({ phaseKey: 'core', status: 'passed' })])).toBe('danger');
  });

  it('is danger when a step carries a blocking verdict even if not failed', () => {
    expect(groupTone([row({ phaseKey: 'decision', status: 'passed', verdict: 'escalate' })])).toBe('danger');
  });

  it('is warn when a step is running (and none failed)', () => {
    expect(groupTone([row({ phaseKey: 'core', status: 'running' }), row({ phaseKey: 'core', status: 'passed' })])).toBe('warn');
  });

  it('is concern when a non-blocking concern needs review', () => {
    expect(groupTone([row({ phaseKey: 'aspect', status: 'passed', verdict: 'concerns' })])).toBe('concern');
  });

  it('is ok when every executable step passed', () => {
    expect(groupTone([row({ phaseKey: 'aspect', status: 'passed' }), row({ phaseKey: 'aspect', status: 'passed' })])).toBe('ok');
  });

  it('is ok when passed steps sit beside disabled ones (disabled is not executable)', () => {
    expect(groupTone([row({ phaseKey: 'pre', status: 'passed' }), row({ phaseKey: 'pre', status: 'disabled' })])).toBe('ok');
  });

  it('is muted when the section is entirely disabled', () => {
    expect(groupTone([row({ phaseKey: 'drift', status: 'disabled' }), row({ phaseKey: 'drift', status: 'disabled' })])).toBe('muted');
  });

  it('is not-run when a settled lightweight attempt did not execute the section', () => {
    expect(groupTone([row({ phaseKey: 'core', status: 'not-run' })])).toBe('not-run');
  });

  it('is neutral when nothing has run yet', () => {
    expect(groupTone([row({ phaseKey: 'tool', status: 'pending' })])).toBe('neutral');
  });

  it('is neutral when passed sits beside pending (not all executable passed)', () => {
    expect(groupTone([row({ phaseKey: 'tool', status: 'passed' }), row({ phaseKey: 'tool', status: 'pending' })])).toBe('neutral');
  });
});

describe('rowIsRisk / rowHasConcern', () => {
  it('flags running and failed rows as risk', () => {
    expect(rowIsRisk(row({ phaseKey: 'core', status: 'running' }))).toBe(true);
    expect(rowIsRisk(row({ phaseKey: 'core', status: 'failed' }))).toBe(true);
  });

  it('flags concern/block verdicts as risk and concern', () => {
    const concern = row({ phaseKey: 'aspect', status: 'passed', verdict: 'concerns' });
    expect(rowIsRisk(concern)).toBe(true);
    expect(rowHasConcern(concern)).toBe(true);
  });

  it('does not treat a plain passed row as risk or concern', () => {
    const ok = row({ phaseKey: 'aspect', status: 'passed', verdict: 'pass' });
    expect(rowIsRisk(ok)).toBe(false);
    expect(rowHasConcern(ok)).toBe(false);
  });
});

describe('groupToneLabel', () => {
  it('maps each tone to a one-word status label (no colour reliance)', () => {
    expect(groupToneLabel('ok')).toBe('Passed');
    expect(groupToneLabel('warn')).toBe('Running');
    expect(groupToneLabel('concern')).toBe('Concerns');
    expect(groupToneLabel('danger')).toBe('Attention');
    expect(groupToneLabel('muted')).toBe('Disabled');
    expect(groupToneLabel('not-run')).toBe('Not run');
    expect(groupToneLabel('neutral')).toBe('Pending');
  });
});

describe('groupAriaLabel', () => {
  it('folds phase, tone, step count and concern count into the accessible name', () => {
    expect(
      groupAriaLabel({
        label: 'ASPECT',
        tone: 'danger',
        stepCount: 4,
        concernCount: 2,
        description: 'Parallel review passes over the finished work.',
      }),
    ).toBe('ASPECT phase, attention, 4 steps, 2 concerns. Parallel review passes over the finished work.');
  });

  it('singularises a one-step section and omits concerns when there are none', () => {
    expect(
      groupAriaLabel({
        label: 'CORE AGENT WORK',
        tone: 'warn',
        stepCount: 1,
        concernCount: 0,
        description: 'The coding agent work.',
      }),
    ).toBe('CORE AGENT WORK phase, running, 1 step. The coding agent work.');
  });
});

describe('buildPipelineGroups contiguity', () => {
  it('breaks a section on each phase change, keeping repeated phases distinct and in order', () => {
    const groups = buildPipelineGroups([
      row({ phaseKey: 'tool', status: 'passed' }),
      row({ phaseKey: 'decision', status: 'passed' }),
      row({ phaseKey: 'tool', status: 'passed' }),
    ]);
    expect(groups.map(g => g.phaseKey)).toEqual(['tool', 'decision', 'tool']);
    expect(groups.map(g => g.key)).toEqual(['tool#0', 'decision#0', 'tool#1']);
  });

  it('merges only contiguous same-phase rows into one section', () => {
    const groups = buildPipelineGroups([
      row({ phaseKey: 'aspect', status: 'passed', totalTokens: 1000 }),
      row({ phaseKey: 'aspect', status: 'passed', totalTokens: 200 }),
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0].stepCount).toBe(2);
    expect(groups[0].totalTokens).toBe(1200);
  });
});

describe('buildPipelineGroups aggregate counters', () => {
  it('counts ran/risk/off/concern and honestly sums tokens', () => {
    const [group] = buildPipelineGroups([
      row({ phaseKey: 'aspect', status: 'passed', verdict: 'pass', totalTokens: 1200 }),
      row({ phaseKey: 'aspect', status: 'passed', verdict: 'concerns', totalTokens: 800 }),
      row({ phaseKey: 'aspect', status: 'failed', totalTokens: 0 }),
      row({ phaseKey: 'aspect', status: 'disabled', totalTokens: 0 }),
    ]);
    expect(group.stepCount).toBe(4);
    expect(group.ranCount).toBe(3); // passed, passed, failed
    expect(group.offCount).toBe(1);
    expect(group.concernCount).toBe(2); // concerns + failed
    expect(group.riskCount).toBe(2); // concerns + failed
    expect(group.totalTokens).toBe(2000);
    expect(group.tone).toBe('danger');
  });
});

describe('default collapse across scenarios', () => {
  // EMPTY: configured pipeline, nothing has run — every section collapses.
  it('empty: collapses every section when nothing has started', () => {
    const groups = buildPipelineGroups([
      row({ phaseKey: 'pre', status: 'pending' }),
      row({ phaseKey: 'core', status: 'pending' }),
      row({ phaseKey: 'drift', status: 'disabled' }),
    ]);
    expect(pipelineStarted(groups.flatMap(g => g.rows))).toBe(false);
    expect(groups.every(g => g.defaultCollapsed)).toBe(true);
  });

  // DONE: quiet completed work recedes until the operator expands it.
  it('done: collapses every quiet completed section', () => {
    const rows = [
      row({ phaseKey: 'pre', status: 'passed', totalTokens: 120 }),
      row({ phaseKey: 'core', status: 'passed', totalTokens: 61000 }),
      row({ phaseKey: 'aspect', status: 'passed', totalTokens: 1200 }),
      row({ phaseKey: 'drift', status: 'disabled' }),
    ];
    expect(pipelineComplete(rows)).toBe(true);
    const groups = buildPipelineGroups(rows);
    const byPhase = Object.fromEntries(groups.map(g => [g.phaseKey, g]));
    expect(byPhase['pre'].defaultCollapsed).toBe(true);
    expect(byPhase['core'].defaultCollapsed).toBe(true);
    expect(byPhase['aspect'].defaultCollapsed).toBe(true);
    expect(byPhase['drift'].defaultCollapsed).toBe(true);
  });

  // BLOCKED: mid-flight with a failed core — danger sections open, quiet
  // finished and pending sections recede.
  it('blocked: opens danger sections and collapses quiet or pending ones', () => {
    const groups = buildPipelineGroups([
      row({ phaseKey: 'pre', status: 'passed', totalTokens: 120 }), // quiet, finished
      row({ phaseKey: 'core', status: 'failed', totalTokens: 47000 }), // danger
      row({ phaseKey: 'decision', status: 'pending' }), // frontier (first waiting)
      row({ phaseKey: 'tool', status: 'pending' }),
    ]);
    const byPhase = Object.fromEntries(groups.map(g => [g.phaseKey, g]));
    expect(byPhase['pre'].defaultCollapsed).toBe(true);
    expect(byPhase['core'].defaultCollapsed).toBe(false);
    expect(byPhase['decision'].defaultCollapsed).toBe(true);
    expect(byPhase['tool'].defaultCollapsed).toBe(true);
  });

  // RUNNING: a live core run keeps the warn section open.
  it('running: opens the running section as warn', () => {
    const groups = buildPipelineGroups([
      row({ phaseKey: 'pre', status: 'passed' }),
      row({ phaseKey: 'core', status: 'running', totalTokens: 12400 }),
      row({ phaseKey: 'aspect', status: 'pending' }),
    ]);
    const byPhase = Object.fromEntries(groups.map(g => [g.phaseKey, g]));
    expect(byPhase['core'].tone).toBe('warn');
    expect(byPhase['core'].defaultCollapsed).toBe(false);
    expect(byPhase['pre'].defaultCollapsed).toBe(true);
  });
});
