/**
 * Frontend projection of the drift-report contract. Mirrors
 * `docs/schemas/drift-report.schema.json` and the backend records in
 * `backend/Services/Drift/DriftReportContract.cs`. Only the slices the
 * project Drift surface consumes today are typed - the rest is intentionally
 * loose so a server-side enum extension does not break the UI.
 */

export type DriftSeverity = 'Info' | 'Warn' | 'High' | 'Critical';
export type DriftFindingStatus = 'New' | 'Accepted' | 'Ignored' | 'Tracked' | 'Resolved';
export type DriftScoreBand = 'Healthy' | 'Watch' | 'Warn' | 'Critical' | 'Unknown';

export interface DriftArchitectureElement {
  elementId: string;
  label: string;
  expectedRole: string;
  score: number;
  severity: DriftSeverity;
  sourceCoverage: number;
  status: DriftFindingStatus;
  evidenceRefs: string[];
  guidelines?: string[] | null;
  allowedDependencies?: string[] | null;
  sourceRefs?: string[] | null;
  summary?: string | null;
  followUpTaskSuggestions?: string[] | null;
}

export interface DriftArchitectureModel {
  modelId: string;
  title: string;
  sourceRef?: string | null;
  elements: DriftArchitectureElement[];
}

export interface ElementStateOverride {
  modelId: string;
  elementId: string;
  status: DriftFindingStatus;
  note?: string | null;
  updatedAt: string;
}

/**
 * Response shape for `GET /api/drift/{project}/architecture`. When `model`
 * is null the project either has no drift report yet or none carries an
 * architecture model. The marble surface treats this as the "no model" state
 * and shows an explanatory empty.
 */
export interface DriftArchitectureSurfaceResponse {
  model: DriftArchitectureModel | null;
  sourceReportId: string | null;
  sourceReportCreatedAt: string | null;
  overrides: ElementStateOverride[];
}
