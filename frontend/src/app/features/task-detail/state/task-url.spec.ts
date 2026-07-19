import { describe, expect, it } from 'vitest';
import { taskUrl, taskUrlKey, withoutTaskUrl } from './task-url';

describe('task URL contract', () => {
  it('uses the stable task key instead of the display slug', () => {
    expect(taskUrlKey({ id: 'human-title-slug', key: 'AGT-2124', displayKey: 'AGT-2124' })).toBe('AGT-2124');
  });

  it('writes a key-only share URL and removes the legacy filesystem locator', () => {
    const current = new URL('http://localhost/?job=human-title-slug&watchPath=C%3A%5Csecret&view=git#diff');
    const next = taskUrl('AGT-2124', current);

    expect(next).toBe('/?view=git&task=AGT-2124#diff');
    expect(next).not.toContain('watchPath');
    expect(next).not.toContain('human-title-slug');
  });

  it('clears only task routing state and preserves shell state', () => {
    const current = new URL('http://localhost/studio?task=AGT-2124&view=git#project=Agent%20Studio');
    expect(withoutTaskUrl(current)).toBe('/studio?view=git#project=Agent%20Studio');
  });
});
