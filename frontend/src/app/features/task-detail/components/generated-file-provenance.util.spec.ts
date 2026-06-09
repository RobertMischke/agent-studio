import { describe, expect, it } from 'vitest';
import { generatedFileProvenance } from './generated-file-provenance.util';

describe('generatedFileProvenance', () => {
  it('formats a compact label and detailed tooltip for generated files', () => {
    const view = generatedFileProvenance({
      file: 'status.md',
      kind: 'status',
      model: 'claude-haiku-4-5',
      cli: 'claude',
      tokensIn: 1200,
      tokensOut: 300,
      tokensTotal: 1500,
      startedAt: '2026-06-09T11:59:58Z',
      endedAt: '2026-06-09T12:00:00Z',
      durationMs: 2000,
      stepId: 'summary-generation',
      runIndex: 2,
      headShaAfter: 'abcdef0123456789',
    });

    expect(view?.label).toBe('claude / claude-haiku-4-5 | 1.5k tokens | 2s');
    expect(view?.tooltip).toContain('File: status.md');
    expect(view?.tooltip).toContain('Step: summary-generation');
    expect(view?.tooltip).toContain('Run: #2');
    expect(view?.tooltip).toContain('Tokens: 1.5k total (1.2k in, 300 out)');
    expect(view?.tooltip).toContain('Commit: abcdef012345');
  });

  it('returns null when no provenance exists', () => {
    expect(generatedFileProvenance(null)).toBeNull();
  });
});
