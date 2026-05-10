/**
 * Wire types for the project Security panel. Mirror the backend records
 * defined in <c>backend/Services/Security/SecurityReviewModels.cs</c>.
 *
 * `parseOk: false` is the load-bearing field: when a review file lacks a
 * structured frontmatter block (or it failed to parse), the panel renders
 * the raw Markdown with an "unstructured report" warning instead of
 * pretending to know the verdict (see the mockup README's "Report
 * Contracts" section).
 */
export interface SecurityReviewSummary {
  fileName: string;
  relPath: string;
  updatedAt: string;
  reviewDate: string | null;
  verdict: string | null;
  severity: string | null;
  openFindings: number | null;
  severities: Record<string, number> | null;
  title: string | null;
  summary: string | null;
  parseOk: boolean;
  parseError: string | null;
}

export interface SecurityReviewListResponse {
  projectName: string;
  reviewsDir: string;
  exists: boolean;
  reviews: SecurityReviewSummary[];
}

export interface SecurityBaselineResponse {
  projectName: string;
  filePath: string;
  exists: boolean;
  status: string | null;
  lastVerified: string | null;
  definitionRef: string | null;
  severityThresholds: Record<string, string> | null;
  summary: string | null;
  parseOk: boolean;
  parseError: string | null;
  markdown: string | null;
}

export interface SecurityAuditQueueResponse {
  jobId: string;
  state: string;
  title: string;
}

export interface SecurityAuditConflictResponse {
  error: string;
  message: string;
  jobId?: string;
  state?: string;
}

/** Baseline badge bucket. Drives the badge tone in the panel header. */
export type SecurityBaselineBadge = 'ok' | 'stale' | 'failing' | 'missing' | 'unknown';
