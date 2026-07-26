import { describe, expect, it } from 'vitest';
import { taskReferenceFromUrl, taskUrl, taskUrlKey, withoutTaskUrl } from './task-url';

describe('task URL contract', () => {
  it('uses the stable task key instead of the display slug', () => {
    expect(taskUrlKey({ id: 'human-title-slug', key: 'AGT-2124', displayKey: 'AGT-2124' })).toBe('AGT-2124');
  });

  it('writes a key-only share URL and removes the legacy filesystem locator', () => {
    const current = new URL('http://localhost/?job=human-title-slug&watchPath=C%3A%5Csecret&view=git#diff');
    const next = taskUrl('AGT-2124', current);

    expect(next).toBe('/?view=git#/tasks/AGT-2124&diff');
    expect(next).not.toContain('watchPath');
    expect(next).not.toContain('human-title-slug');
  });

  it('removes a competing shell route while preserving independent hash state', () => {
    const current = new URL(
      'http://localhost/?view=git#/projects/PROJ-002/wiki?page=concepts%2Foverview.md&filters=type%3Abug',
    );

    expect(taskUrl('AGT-2124', current))
      .toBe('/?view=git#/tasks/AGT-2124&filters=type%3Abug');
  });

  it('clears only task routing state and preserves shell state', () => {
    const current = new URL('http://localhost/studio?view=git#/tasks/AGT-2124&filters=type%3Abug');
    expect(withoutTaskUrl(current)).toBe('/studio?view=git#filters=type%3Abug');
  });

  it('reads the hash route and retains query compatibility', () => {
    expect(taskReferenceFromUrl(new URL('http://localhost/#/tasks/AGT-2124?view=timeline%3Aactivity')))
      .toBe('AGT-2124');
    expect(taskReferenceFromUrl(new URL('http://localhost/?task=AGT-2124')))
      .toBe('AGT-2124');
  });
});
