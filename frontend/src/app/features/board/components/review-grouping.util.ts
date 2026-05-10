import { JobInfo } from '../../../models/job.model';

/**
 * Splits 4-review jobs into the two swim-lane sub-sections rendered by
 * <app-job-column>. Pure so it stays testable without Angular's TestBed.
 *
 * Routing rule: any job with a non-null `orchestratorVerdict` belongs in
 * the "Orchestrator review" sub-section; everything else falls through to
 * "Human review". The verdict values map 1:1 to the per-project decision
 * journal kinds (`reissue` / `escalate` / `accept`); the literal `pending`
 * value is reserved for forward compatibility.
 */
export interface ReviewSubSection {
  readonly kind: 'orchestrator' | 'human';
  readonly label: string;
  readonly icon: string;
  readonly jobs: JobInfo[];
}

export function groupReviewJobs(jobs: readonly JobInfo[]): readonly ReviewSubSection[] {
  const orchestrator: JobInfo[] = [];
  const human: JobInfo[] = [];
  for (const j of jobs) {
    if (j.orchestratorVerdict) orchestrator.push(j);
    else human.push(j);
  }
  return [
    { kind: 'orchestrator', label: 'Orchestrator review', icon: '🤖', jobs: orchestrator },
    { kind: 'human',        label: 'Human review',        icon: '👤', jobs: human }
  ];
}
