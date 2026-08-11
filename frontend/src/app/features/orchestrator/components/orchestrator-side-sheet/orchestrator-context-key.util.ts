import type { OrchestratorContextSession } from '../../models/orchestrator.model';

export interface ParsedOrchestratorContextKey {
  key: string;
  kind: 'global' | 'project' | 'task' | 'dossier';
  projectId: string | null;
  taskKey: string | null;
  dossierId?: string | null;
}

function validPart(value: string): boolean {
  return value.length > 0
    && value === value.trim()
    && !value.includes('/')
    && !value.includes('\\')
    && ![...value].some(char => {
      const code = char.charCodeAt(0);
      return code < 32 || (code >= 127 && code <= 159);
    });
}

/** Mirrors the strict backend OrchestratorContextKey parser. */
export function parseOrchestratorContextKey(raw: string | null | undefined): ParsedOrchestratorContextKey | null {
  if (!raw || raw !== raw.trim()) return null;
  if (raw === 'global') {
    return { key: raw, kind: 'global', projectId: null, taskKey: null };
  }
  if (raw.startsWith('project:')) {
    const projectId = raw.slice('project:'.length);
    return validPart(projectId)
      ? { key: raw, kind: 'project', projectId, taskKey: null }
      : null;
  }
  if (raw.startsWith('task:')) {
    const rest = raw.slice('task:'.length);
    const slash = rest.indexOf('/');
    if (slash < 0) return null;
    const projectId = rest.slice(0, slash);
    const taskKey = rest.slice(slash + 1);
    return validPart(projectId) && validPart(taskKey)
      ? { key: raw, kind: 'task', projectId, taskKey }
      : null;
  }
  if (raw.startsWith('dossier:')) {
    const rest = raw.slice('dossier:'.length);
    const slash = rest.indexOf('/');
    if (slash < 0) return null;
    const projectId = rest.slice(0, slash);
    const dossierId = rest.slice(slash + 1);
    return validPart(projectId) && validPart(dossierId)
      ? { key: raw, kind: 'dossier', projectId, taskKey: null, dossierId }
      : null;
  }
  return null;
}

export function buildNavigationContextKey(
  project: string | null,
  taskKey: string | null,
  dossierId: string | null = null,
): string | null {
  const canonicalProject = project?.trim() ?? '';
  const canonicalTask = taskKey?.trim() ?? '';
  const canonicalDossier = dossierId?.trim() ?? '';
  if (!validPart(canonicalProject)) return null;
  if (canonicalTask && validPart(canonicalTask)) return 'task:' + canonicalProject + '/' + canonicalTask;
  if (canonicalDossier && validPart(canonicalDossier)) return 'dossier:' + canonicalProject + '/' + canonicalDossier;
  return 'project:' + canonicalProject;
}

export interface EffectiveContextKeyResult {
  key: string | null;
  discardedSelection: boolean;
}

/**
 * Resolve the one key shared by transcript reads, digest reads, refreshes, and
 * sends. A chat-switcher selection is valid only while navigation remains at
 * the scope where it was selected and the selected scope still exists.
 */
export function resolveEffectiveContextKey(
  navigationKey: string | null,
  selectedKey: string | null,
  selectionNavigationKey: string | null,
  projects: readonly string[],
  sessions: readonly OrchestratorContextSession[],
): EffectiveContextKeyResult {
  const navigation = parseOrchestratorContextKey(navigationKey);
  if (!selectedKey) return { key: navigation?.key ?? null, discardedSelection: false };

  const selected = parseOrchestratorContextKey(selectedKey);
  const selectionStillAnchored = selectionNavigationKey === (navigation?.key ?? null);
  const selectedStillExists = selected?.kind === 'global'
    || (selected?.kind === 'project' && projects.includes(selected.projectId!))
    || (selected?.kind === 'task' && sessions.some(session =>
      session.contextKey === selected.key
      && session.kind === 'task'
      && session.projectId === selected.projectId
      && session.taskKey === selected.taskKey))
    || (selected?.kind === 'dossier' && sessions.some(session =>
      session.contextKey === selected.key
      && session.kind === 'dossier'
      && session.projectId === selected.projectId
      && session.dossierId === selected.dossierId));

  if (!selected || !selectionStillAnchored || !selectedStillExists) {
    return { key: navigation?.key ?? null, discardedSelection: true };
  }
  return { key: selected.key, discardedSelection: false };
}

export function orchestratorContextErrorMessage(error: unknown, fallback: string): string {
  const candidate = error as { error?: { error?: unknown }; message?: unknown } | null;
  const rawMessage = candidate?.error?.error ?? candidate?.message;
  const technical = typeof rawMessage === 'string' && rawMessage.trim() ? rawMessage : fallback;
  return technical.includes('Invalid orchestrator context key')
    ? 'This chat context is no longer available. Return to the current task or board, or open the current Dossier, then try again.'
    : technical;
}
