import type { OrchestratorContextReference } from './orchestrator.model';

export type OrchestratorContextSourceCategory =
  | 'current'
  | 'tasks'
  | 'wiki'
  | 'files'
  | 'commits';

/** Host-owned display metadata for one stable, send-time-resolved reference. */
export interface OrchestratorContextSourceOption {
  id: string;
  category: OrchestratorContextSourceCategory;
  label: string;
  detail: string;
  estimateTokens: number;
  reference: OrchestratorContextReference;
}

export function contextSourceId(reference: OrchestratorContextReference): string {
  return `${reference.kind}:${reference.projectId ?? ''}:${reference.reference}`;
}
