/**
 * Wire types for the project UX/UI panel (slice 6 of the quality-system
 * mockup, docs/mockups/quality-system/). Mirror the backend records in
 * <c>backend/Services/Design/DesignEvidenceModels.cs</c>.
 *
 * `parseOk: false` is the load-bearing field: when a council note or
 * reference lacks a structured frontmatter block, the panel renders raw
 * Markdown with an "unstructured report" warning instead of inventing
 * fields (see the mockup README's "Report Contracts" section).
 */

export interface DesignOverviewResponse {
  projectName: string;
  designDir: string;
  exists: boolean;
  status: string;
  statusDetail: string | null;
  lastReviewDate: string | null;
  referencesCount: number;
  screenshotsAcceptedCount: number;
  screenshotsRejectedCount: number;
  externalCount: number;
  councilOpenCount: number;
  councilAcceptedCount: number;
  briefExists: boolean;
  briefSummary: string | null;
}

export type DesignReferenceKind = 'accepted' | 'rejected' | 'external' | 'brief' | string;

export interface DesignReferenceItem {
  fileName: string;
  relPath: string;
  kind: DesignReferenceKind;
  title: string | null;
  summary: string | null;
  screenshotRelPath: string | null;
  updatedAt: string;
  parseOk: boolean;
  parseError: string | null;
}

export interface DesignReferencesResponse {
  projectName: string;
  referencesDir: string;
  exists: boolean;
  references: DesignReferenceItem[];
}

export interface DesignCouncilNote {
  fileName: string;
  relPath: string;
  category: string | null;
  title: string | null;
  summary: string | null;
  noteDate: string | null;
  acceptedAt: string | null;
  updatedAt: string;
  parseOk: boolean;
  parseError: string | null;
}

export interface DesignCouncilResponse {
  projectName: string;
  councilDir: string;
  exists: boolean;
  notes: DesignCouncilNote[];
}

export interface DesignActionQueueResponse {
  jobId: string;
  state: string;
  title: string;
}

export interface DesignActionConflictResponse {
  error: string;
  message: string;
  jobId?: string;
  state?: string;
}

export interface AcceptCouncilNoteResponse {
  fileName: string;
  acceptedAt: string;
  parseOk: boolean;
}

/** The four action buttons in the design loop band. */
export type DesignActionKind =
  | 'screenshot-critique'
  | 'council-review'
  | 'request-next-version';
