import { describe, expect, it } from 'vitest';
import {
  createSmileyMask,
  EMPTY_STATE_COLS,
  EMPTY_STATE_ROWS,
  emptyStateFrame,
} from './studio-empty-state.animation';

describe('studio empty-state animation', () => {
  it('moves through the calm chaos, formation, smiley, and decay cycle', () => {
    expect(emptyStateFrame(0).phase).toBe('chaos');
    expect(emptyStateFrame(4_000).phase).toBe('forming');
    expect(emptyStateFrame(6_000).phase).toBe('smiley');
    expect(emptyStateFrame(8_000).phase).toBe('decay');
    expect(emptyStateFrame(10_500).phase).toBe('chaos');
    expect(emptyStateFrame(12_000).phase).toBe('chaos');
  });

  it('builds three distinct smiley formations across the canvas', () => {
    const mask = createSmileyMask();
    const thirds = [0, 0, 0];
    for (let index = 0; index < mask.length; index++) {
      if (!mask[index]) continue;
      const x = index % EMPTY_STATE_COLS;
      thirds[Math.min(2, Math.floor(x / (EMPTY_STATE_COLS / 3)))]++;
    }

    expect(mask).toHaveLength(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
    expect(thirds.every(count => count > 40)).toBe(true);
  });
});
