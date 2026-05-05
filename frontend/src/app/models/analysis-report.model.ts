// Mirrors backend/Services/Analysis/AnalysisReportContract.cs and
// docs/schemas/analysis-report.schema.json. Kept narrow: only the slice the
// project-level Analysis Reports surface renders is duplicated here.

export type AnalysisReportScopeKind = 'Workspace' | 'Project' | 'Task' | 'Run' | 'TimeWindow';
export type AnalysisReportProducerKind = 'Manual' | 'Scheduled' | 'MetaCycle' | 'SupportingAgent' | 'ExternalMonitor';
export type AnalysisReportTrigger = AnalysisReportProducerKind;
export type AnalysisReportSeverity = 'Info' | 'Warn' | 'High' | 'Critical';
export type AnalysisReportParseStatus = 'Structured' | 'Unstructured' | 'MalformedJson';
export type AnalysisReportReferenceKind =
  | 'Job' | 'Run' | 'Commit' | 'Screenshot' | 'BusMessage' | 'RuntimeEvent' | 'PreviousReport' | 'LogSlice' | 'Doc';

export interface AnalysisReportTimeWindow { from: string; to: string; }

export interface AnalysisReportScope {
  kind: AnalysisReportScopeKind;
  project?: string | null;
  jobId?: string | null;
  runIndex?: number | null;
  timeWindow?: AnalysisReportTimeWindow | null;
}

export interface AnalysisReportProducer {
  kind: AnalysisReportProducerKind;
  participantId?: string | null;
  agent?: string | null;
}

export interface AnalysisReportReference {
  kind: AnalysisReportReferenceKind;
  ref: string;
  label?: string | null;
}

export interface AnalysisReportFollowUpTaskSuggestion {
  title: string;
  summary: string;
  priority: 'Low' | 'Normal' | 'High' | 'Critical';
  relatedTopic?: string | null;
  targetState?: string | null;
  createdJobId?: string | null;
}

export interface AnalysisReportFinding {
  topic: string;
  severity: AnalysisReportSeverity;
  message: string;
  evidenceRefs?: string[] | null;
}

export interface AnalysisReport {
  reportId: string;
  createdAt: string;
  scope: AnalysisReportScope;
  producer: AnalysisReportProducer;
  trigger: AnalysisReportTrigger;
  topic: string;
  summary: string;
  severity: AnalysisReportSeverity;
  parseStatus: AnalysisReportParseStatus;
  references: AnalysisReportReference[];
  followUpTaskSuggestions: AnalysisReportFollowUpTaskSuggestion[];
  parseError?: string | null;
  tags?: string[] | null;
  findings?: AnalysisReportFinding[] | null;
  markdownPath?: string | null;
  schemaVersion: number;
}

export interface AnalysisReportListResponse {
  reports: AnalysisReport[];
}

export interface AnalysisReportDetailResponse {
  report: AnalysisReport;
  markdown: string | null;
}

/**
 * Manual-trigger topic catalogue. Drives the project-level "Analysis Reports"
 * surface buttons and the schedule rows. Topics are intentionally fixed so the
 * surface stays predictable; new topics land here when their producer code
 * path lands. Slugs are camelCase to match the schedule map keys on the
 * backend.
 */
export const ANALYSIS_TOPICS: ReadonlyArray<{ slug: string; label: string; description: string }> = [
  { slug: 'roadmapAlignment', label: 'Roadmap alignment', description: 'Are queued tasks and active work aligned with the ROADMAP?' },
  { slug: 'queueHealth', label: 'Queue health', description: 'Backlog shape, blocked / stalled tasks, lane balance.' },
  { slug: 'docsDrift', label: 'Docs drift', description: 'Docs (README, AGENTS, ADR) lagging behind code or product changes.' },
  { slug: 'staleJobs', label: 'Stale jobs', description: 'Tasks that have been parked or untouched too long.' },
  { slug: 'tokenSpend', label: 'Token spend review', description: 'Spend by job / run / model and outlier flags.' },
  { slug: 'qaStatus', label: 'QA status', description: 'Test pass rate, flake rate, coverage gaps in changed code.' },
];

/** Cadence options for scheduled reports. Default is `disabled`. */
export const ANALYSIS_CADENCES: ReadonlyArray<{ id: string; label: string; tooltip: string }> = [
  { id: 'disabled', label: 'Disabled', tooltip: 'No scheduled run. Manual trigger remains available.' },
  { id: 'fewHours', label: 'Every few hours', tooltip: 'Run on a multi-hour cadence.' },
  { id: 'daily', label: 'Daily', tooltip: 'Run once a day.' },
  { id: 'manualOnly', label: 'Manual only', tooltip: 'Same as disabled: produced only by user clicks.' },
];
