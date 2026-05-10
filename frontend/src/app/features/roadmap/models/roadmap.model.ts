/**
 * Cycle 9 roadmap-intake feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Splitter that takes a roadmap-style brief and proposes candidate
 * tasks the user reviews + edits in place. The confirm step
 * materialises the accepted ones as job folders in 1-preparation.
 */

export interface RoadmapIntakeCandidate {
  title: string;
  promptBody: string;
  kind: 'feature' | 'bug' | 'adr' | 'chore' | 'research' | string;
  suggestedOrder: number;
  suggestedCliType: string;
  rationale: string;
}

export interface RoadmapIntakeResponse {
  candidates: RoadmapIntakeCandidate[];
  notes: string;
}

export interface RoadmapIntakeConfirmResponse {
  created: { jobId: string; title: string; state: string }[];
  skipped: string[];
}
