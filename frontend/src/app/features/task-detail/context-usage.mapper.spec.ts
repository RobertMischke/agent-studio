import { describe, expect, it } from 'vitest';
import type { ContextUsageSnapshot } from '../../models/task.model';
import { toChatContextUsage } from './context-usage.mapper';

const snapshot = (overrides: Partial<ContextUsageSnapshot> = {}): ContextUsageSnapshot => ({
  at: '2026-07-05T10:00:00.000Z',
  command: '/context',
  status: 'ok',
  error: null,
  metrics: [],
  sections: [],
  notes: [],
  rawText: '',
  ...overrides,
});

describe('toChatContextUsage', () => {
  it('returns null without a snapshot or without a used/max pair', () => {
    expect(toChatContextUsage(null)).toBeNull();
    expect(toChatContextUsage(snapshot({ rawText: 'no numbers here' }))).toBeNull();
  });

  it('extracts used/max from the k-suffixed header line', () => {
    const usage = toChatContextUsage(
      snapshot({ rawText: 'claude-sonnet-5 · 76.4k/200k tokens (38%)' }),
    );
    expect(usage).not.toBeNull();
    expect(usage!.usedTokens).toBe(76_400);
    expect(usage!.maxTokens).toBe(200_000);
    expect(usage!.capturedAt).toBe('2026-07-05T10:00:00.000Z');
    expect(usage!.sourceLabel).toBe('via /context');
  });

  it('finds the pair in metrics when the raw text has none', () => {
    const usage = toChatContextUsage(
      snapshot({ metrics: [{ label: 'Tokens', value: '120000 / 1000000 tokens' }] }),
    );
    expect(usage!.usedTokens).toBe(120_000);
    expect(usage!.maxTokens).toBe(1_000_000);
  });

  it('maps token-count metrics to breakdown sections and skips prose metrics', () => {
    const usage = toChatContextUsage(
      snapshot({
        rawText: '76k/200k tokens',
        metrics: [
          { label: 'System prompt', value: '3.1k tokens (1.6%)' },
          { label: 'Messages', value: '55.1k tokens (27.6%)' },
          { label: 'Model', value: 'claude-sonnet-5' },
        ],
      }),
    );
    expect(usage!.sections).toEqual([
      { label: 'System prompt', tokens: 3_100 },
      { label: 'Messages', tokens: 55_100 },
    ]);
  });
});
