// Mirrors backend/Services/ProjectSteeringDocsService.cs DTOs.
// Kept narrow: only the slice the UI needs.

export type SteeringDocsSourceKind =
  | 'projectReadme'
  | 'agentInstructions'
  | 'agentCliShim'
  | 'roadmap'
  | 'taskContract'
  | 'skillsLookup'
  | 'adrIndex'
  | 'runtimePrompt'
  | 'projectSettings'
  | 'steeringNote';

export type SteeringDocsWarningSeverity = 'info' | 'warn' | 'high';

export type SteeringDocsWarningKind =
  | 'missingSource'
  | 'stale'
  | 'possibleConflict'
  | 'recurringFailure'
  | 'gatewayTooHeavy';

export interface SteeringDocsSourceChild {
  name: string;
  relPath: string;
  updatedAt: string;
  size: number;
}

export interface SteeringDocsSource {
  id: string;
  label: string;
  relPath: string;
  kind: SteeringDocsSourceKind;
  why: string;
  exists: boolean;
  updatedAt: string | null;
  size: number;
  appliesToClis: string[];
  children: SteeringDocsSourceChild[] | null;
}

export interface SteeringDocsWarning {
  severity: SteeringDocsWarningSeverity;
  kind: SteeringDocsWarningKind;
  message: string;
  sourceId: string | null;
  evidenceRefs: string[];
}

export interface SteeringDocsOverview {
  projectName: string;
  baseDir: string;
  sources: SteeringDocsSource[];
  warnings: SteeringDocsWarning[];
  lastUpdated: string | null;
}

export interface SteeringFileContent {
  relPath: string;
  content: string;
}

// Mirrors backend/Features/Docs/AgentDocsReadAnalyticsService.cs DTOs.
// Real Tool-Use Read Analytics behind the former Agent Docs mockup.

export interface AgentDocsReadCliCount {
  cli: string;
  reads: number;
}

export interface AgentDocsReadFile {
  relPath: string;
  label: string;
  reads: number;
  recentReads: number;
  taskCount: number;
  lastReadAt: string | null;
  byCli: AgentDocsReadCliCount[];
}

export interface AgentDocsReadCliTotal {
  cli: string;
  reads: number;
}

export interface AgentDocsReadAnalytics {
  projectName: string;
  baseDir: string;
  windowDays: number;
  hasData: boolean;
  totalReads: number;
  recentReads: number;
  taskCount: number;
  lastReadAt: string | null;
  files: AgentDocsReadFile[];
  byCli: AgentDocsReadCliTotal[];
  generatedAt: string;
}
