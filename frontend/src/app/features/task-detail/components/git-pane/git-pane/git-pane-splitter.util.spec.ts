import { describe, expect, it } from 'vitest';
import { clampTreeWidth } from './git-pane-splitter.util';

describe('clampTreeWidth', () => {
  it('keeps fixed floors on roomy panes and proportional floors under pressure', () => {
    expect(clampTreeWidth(50, 1000)).toBe(200);
    expect(clampTreeWidth(300.4, 1000)).toBe(300);
    expect(clampTreeWidth(900, 1000)).toBe(679);
    expect(clampTreeWidth(500, 400)).toBe(159);
  });

  it('only applies the tree floor while the container width is unknown', () => {
    expect(clampTreeWidth(5000, 0)).toBe(5000);
    expect(clampTreeWidth(10, 0)).toBe(200);
  });
});
