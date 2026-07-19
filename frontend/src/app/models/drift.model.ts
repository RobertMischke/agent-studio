/**
 * Frontend projection of the drift-report contract. Mirrors
 * `docs/app/schemas/drift-report.schema.json` and the backend records in
 * `backend/Services/Drift/DriftReportContract.cs`. Only the slices the
 * project Drift surface consumes today are typed - the rest is intentionally
 * loose so a server-side enum extension does not break the UI.
 */

export type DriftSeverity = 'Info' | 'Warn' | 'High' | 'Critical';
export type DriftFindingStatus = 'New' | 'Accepted' | 'Ignored' | 'Tracked' | 'Resolved';
export type DriftScoreBand = 'Healthy' | 'Watch' | 'Warn' | 'Critical' | 'Unknown';
export type DriftReportTrigger = 'Manual' | 'Scheduled' | 'MetaCycle' | 'SupportingAgent' | 'ExternalMonitor';
export type DriftReportParseStatus = 'Structured' | 'Unstructured' | 'MalformedJson';
export type DriftDimensionType =
  | 'Intent'
  | 'Spec'
  | 'TaskJob'
  | 'Architecture'
  | 'Documentation'
  | 'Marketing'
  | 'Design'
  | 'Test'
  | 'Runtime'
  | 'Process'
  | 'Schema'
  | 'Token';
export type DriftFollowUpPriority = 'Low' | 'Normal' | 'High' | 'Critical';

export interface DriftFinding {
  findingId: string;
  severity: DriftSeverity;
  summary: string;
  status: DriftFindingStatus;
  firstSeenAt?: string | null;
  lastSeenAt?: string | null;
  trackedTaskId?: string | null;
  evidenceRefs?: string[] | null;
}

export interface DriftFindingSeverityCounts {
  info?: number;
  warn?: number;
  high?: number;
  critical?: number;
}

export interface DriftScoreInputs {
  findingsBySeverity?: DriftFindingSeverityCounts | null;
  affectedSurfaces?: string[] | null;
  recurrenceCount?: number;
  oldestFindingAgeDays?: number | null;
  trackedFindings?: number;
  totalFindings?: number;
}

export interface DriftDimension {
  type: DriftDimensionType;
  score: number;
  severity: DriftSeverity;
  confidence: number;
  sourceCoverage: number;
  status: DriftFindingStatus;
  summary: string;
  evidenceRefs: string[];
  recommendedActions: string[];
  scoreInputs?: DriftScoreInputs | null;
  findings?: DriftFinding[] | null;
}

export interface DriftFollowUpSuggestion {
  title: string;
  summary?: string | null;
  priority: DriftFollowUpPriority;
  relatedDimension?: DriftDimensionType | null;
  targetState?: string | null;
  createdJobId?: string | null;
}

export interface DriftReportScope {
  kind: 'Project' | 'Workspace' | 'Task' | 'Run' | 'TimeWindow';
  taskId?: string | null;
  runIndex?: number | null;
  timeWindow?: { from: string; to: string } | null;
  sourceRefs?: string[] | null;
}

export interface DriftReport {
  reportId: string;
  project: string;
  createdAt: string;
  trigger: DriftReportTrigger;
  scope: DriftReportScope;
  overallScore: number;
  scoreBand: DriftScoreBand;
  summary: string;
  parseStatus: DriftReportParseStatus;
  parseError?: string | null;
  tags?: string[] | null;
  dimensions: DriftDimension[];
  architectureModel?: DriftArchitectureModel | null;
  followUpTaskSuggestions: DriftFollowUpSuggestion[];
  schemaVersion?: number;
  markdownPath?: string | null;
  producer?: { kind: string; participantId?: string | null; agent?: string | null } | null;
}

export interface DriftReportListResponse {
  reports: DriftReport[];
}

export interface DriftReportDetailResponse {
  report: DriftReport;
  markdown: string | null;
}

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
