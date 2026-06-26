import { describe, it, expect } from 'vitest';
import { resolveRole, getRole, ROLE_CATALOGUE, type WorkforceRoleId } from './workforce-role';

describe('workforce-role catalogue', () => {
  it('has at least the minimum roles required by the workforce prompt', () => {
    const ids = new Set(ROLE_CATALOGUE.map((r) => r.id));
    for (const required of [
      'task-executor',
      'code-reviewer',
      'architecture-custodian',
      'security-auditor',
      'test-author',
      'documentation-maintainer',
      'plan-curator',
      'agent-generic',
    ] as WorkforceRoleId[]) {
      expect(ids.has(required), `missing role ${required}`).toBe(true);
    }
  });

  it('every role exposes label, description, accent, glyph', () => {
    for (const role of ROLE_CATALOGUE) {
      expect(role.label.length, role.id).toBeGreaterThan(0);
      expect(role.description.length, role.id).toBeGreaterThan(0);
      expect(role.accent.length, role.id).toBeGreaterThan(0);
      expect(role.glyph.length, role.id).toBeGreaterThan(0);
    }
  });

  it('getRole returns the agent-generic fallback when an unknown id is asked for', () => {
    const role = getRole('not-a-real-role' as WorkforceRoleId);
    expect(role.id).toBe('agent-generic');
  });
});

describe('resolveRole — deterministic mapping', () => {
  it('explicit roleId wins over everything', () => {
    const role = resolveRole({
      roleId: 'security-auditor',
      author: 'claude',
      kind: 'turn',
      refs: ['aspect:code-quality'],
    });
    expect(role.id).toBe('security-auditor');
  });

  it('aspect refs map to the matching role', () => {
    expect(resolveRole({ author: 'claude', refs: ['aspect:code-quality'] }).id).toBe('code-reviewer');
    expect(resolveRole({ author: 'codex', refs: ['aspect:requirement-fit'] }).id).toBe('plan-curator');
    expect(resolveRole({ author: 'gemini', refs: ['aspect:documentation-impact'] }).id).toBe('documentation-maintainer');
    expect(resolveRole({ author: 'agent', refs: ['aspect:tests-and-evidence'] }).id).toBe('test-author');
  });

  it('explicit role refs route through the catalogue', () => {
    expect(resolveRole({ author: 'agent', refs: ['role:security-auditor'] }).id).toBe('security-auditor');
  });

  it('user author always maps to the user role', () => {
    expect(resolveRole({ author: 'user', kind: 'turn' }).id).toBe('user');
  });

  it('orchestrator and supervisor map to themselves', () => {
    expect(resolveRole({ author: 'orchestrator', kind: 'turn' }).id).toBe('orchestrator');
    expect(resolveRole({ author: 'supervisor', kind: 'turn' }).id).toBe('supervisor');
  });

  it('CLI-typed agents default to Task Executor', () => {
    for (const author of ['agent', 'claude', 'codex', 'gemini']) {
      expect(resolveRole({ author, kind: 'turn' }).id, author).toBe('task-executor');
    }
  });

  it('decision and watchdog kinds map even when the author is generic', () => {
    expect(resolveRole({ author: 'agent', kind: 'event-decision' }).id).toBe('orchestrator');
    expect(resolveRole({ author: 'agent', kind: 'event-watchdog' }).id).toBe('health-officer');
    expect(resolveRole({ author: 'agent', kind: 'supervisor.wait' }).id).toBe('health-officer');
  });

  it('unknown author renders the agent-generic fallback rather than crashing', () => {
    expect(resolveRole({ author: 'martian-cli', kind: 'turn' }).id).toBe('agent-generic');
    expect(resolveRole({}).id).toBe('agent-generic');
    expect(resolveRole({ author: null, kind: null, refs: null }).id).toBe('agent-generic');
  });

  it('is deterministic: same input → same role across many calls', () => {
    const input = { author: 'claude', kind: 'turn', refs: ['aspect:code-quality'] };
    const seen = new Set<string>();
    for (let i = 0; i < 50; i++) seen.add(resolveRole(input).id);
    expect(seen.size).toBe(1);
  });
});
