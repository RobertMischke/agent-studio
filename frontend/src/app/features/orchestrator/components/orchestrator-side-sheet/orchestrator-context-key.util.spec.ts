import { describe, expect, it } from 'vitest';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import {
  buildNavigationContextKey,
  orchestratorContextErrorMessage,
  parseOrchestratorContextKey,
  resolveEffectiveContextKey,
} from './orchestrator-context-key.util';

function taskSession(contextKey: string, projectId: string, taskKey: string): OrchestratorContextSession {
  return {
    contextKey, kind: 'task', projectId, taskKey, updatedAt: '', model: null,
    cumulativeInputTokens: 0, cumulativeOutputTokens: 0,
    cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
    runtimeStatus: 'idle', queuePosition: 0,
  };
}

describe('orchestrator context key resolution', () => {
  it('builds and parses a canonical task key when the project contains spaces', () => {
    const key = buildNavigationContextKey(' Agent Studio ', ' AGT-2149 ');

    expect(key).toBe('task:Agent Studio/AGT-2149');
    expect(parseOrchestratorContextKey(key)).toEqual({
      key, kind: 'task', projectId: 'Agent Studio', taskKey: 'AGT-2149',
    });
  });

  it('rejects every control-character range rejected by the backend parser', () => {
    expect(parseOrchestratorContextKey('project:Agent\u0085Studio')).toBeNull();
    expect(parseOrchestratorContextKey('task:Agent Studio/AGT-21\u009f49')).toBeNull();
  });

  it('builds and parses a route-derived Dossier context key', () => {
    const key = buildNavigationContextKey('Agent Studio', null, 'context-model');

    expect(key).toBe('dossier:Agent Studio/context-model');
    expect(parseOrchestratorContextKey(key)).toEqual({
      key,
      kind: 'dossier',
      projectId: 'Agent Studio',
      taskKey: null,
      dossierId: 'context-model',
    });
  });

  it('falls back to current navigation for invalid and stale selections', () => {
    const navigation = 'task:Agent Studio/AGT-2149';
    expect(resolveEffectiveContextKey(
      navigation, 'task:broken/extra/slash', navigation, ['Agent Studio'], [],
    )).toEqual({ key: navigation, discardedSelection: true });

    expect(resolveEffectiveContextKey(
      navigation,
      'task:Agent Studio/AGT-2000',
      'project:Agent Studio',
      ['Agent Studio'],
      [taskSession('task:Agent Studio/AGT-2000', 'Agent Studio', 'AGT-2000')],
    )).toEqual({ key: navigation, discardedSelection: true });
  });

  it('keeps a valid session selection until navigation changes', () => {
    const navigation = 'project:Agent Studio';
    const selected = 'task:Agent Studio/AGT-2149';
    expect(resolveEffectiveContextKey(
      navigation, selected, navigation, ['Agent Studio'],
      [taskSession(selected, 'Agent Studio', 'AGT-2149')],
    )).toEqual({ key: selected, discardedSelection: false });
  });

  it('replaces the internal parser error with an actionable message', () => {
    expect(orchestratorContextErrorMessage(
      { error: { error: 'Invalid orchestrator context key.' } }, 'Failed to send',
    )).toContain('Return to the current task or board');
    expect(orchestratorContextErrorMessage(
      { error: { error: { code: 'bad-context' } } }, 'Failed to send',
    )).toBe('Failed to send');
  });
});
