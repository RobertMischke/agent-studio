import { describe, expect, it } from 'vitest';
import {
  PIPELINE_DENSITY_STORAGE_KEY,
  pipelineMetricVisibility,
  readPipelineDensity,
  uniformGroupActivation,
  uniformGroupModel,
  writePipelineDensity,
} from './pipeline-panel-density.util';

describe('pipeline panel density helpers', () => {
  it('defaults to compact and persists either density', () => {
    const values = new Map<string, string>();
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
    };
    expect(readPipelineDensity(storage)).toBe('compact');
    writePipelineDensity('comfortable', storage);
    expect(values.get(PIPELINE_DENSITY_STORAGE_KEY)).toBe('comfortable');
    expect(readPipelineDensity(storage)).toBe('comfortable');
  });

  it('shows only metric columns backed by real row data', () => {
    expect(pipelineMetricVisibility([
      { status: 'pending', startedAt: null, durationMs: 0, totalTokens: 0, costKnown: true },
    ])).toEqual({ time: false, duration: false, tokens: false, cost: false, any: false });
    expect(pipelineMetricVisibility([
      { status: 'passed', startedAt: '2026-07-22T10:00:00Z', durationMs: 800, totalTokens: 1200, costKnown: true },
    ])).toEqual({ time: true, duration: true, tokens: true, cost: true, any: true });
    expect(pipelineMetricVisibility([
      { status: 'passed', startedAt: null, durationMs: 0, totalTokens: 1200, costKnown: false },
    ])).toEqual({ time: false, duration: false, tokens: true, cost: false, any: true });
  });

  it('lifts only metadata shared by multiple rows', () => {
    const activation = { state: 'active', source: 'global', reason: 'Workspace default' } as const;
    const rows = [
      { model: 'claude-haiku-4-5', thinkingLevel: null, config: { activation } },
      { model: 'claude-haiku-4-5', thinkingLevel: null, config: { activation } },
    ];
    expect(uniformGroupModel(rows)).toBe('claude-haiku-4-5');
    expect(uniformGroupActivation(rows)).toEqual(activation);
    expect(uniformGroupModel([...rows, { ...rows[0], model: 'claude-sonnet-4-6' }])).toBeNull();
    expect(uniformGroupActivation(rows.map(row => ({ ...row, config: null })))).toBeNull();
  });
});
