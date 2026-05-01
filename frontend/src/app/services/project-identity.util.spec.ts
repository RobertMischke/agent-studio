import { describe, expect, it } from 'vitest';
import { projectIdentity } from './project-identity.util';

describe('projectIdentity', () => {
  it('uses the first alphanumeric character as the initial, uppercased', () => {
    expect(projectIdentity('Agent Task Processor').initial).toBe('A');
    expect(projectIdentity('runbook').initial).toBe('R');
    expect(projectIdentity('  42-foo').initial).toBe('4');
    expect(projectIdentity('').initial).toBe('?');
  });

  it('is deterministic — same name always produces the same hue', () => {
    const a = projectIdentity('Agent Task Processor');
    const b = projectIdentity('Agent Task Processor');
    expect(a.hue).toBe(b.hue);
    expect(a.color).toBe(b.color);
  });

  it('assigns different hues to two distinct project names', () => {
    // Not a guarantee for arbitrary strings, but for the two real watched
    // projects it must hold — otherwise the whole feature is pointless.
    const a = projectIdentity('Agent Task Processor');
    const b = projectIdentity('Runbook');
    expect(a.hue).not.toBe(b.hue);
  });

  it('produces colour strings that reference the chosen hue', () => {
    const id = projectIdentity('Some Project');
    expect(id.color).toContain(`${id.hue}`);
    expect(id.soft).toContain(`${id.hue}`);
    expect(id.border).toContain(`${id.hue}`);
  });
});
