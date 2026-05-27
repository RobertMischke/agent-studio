export type SpecChangeCategory = 'Intended' | 'AtRisk' | 'Drift';

export interface SpecChangeEntry {
  path: string;
  fileName: string;
  gitStatus: string;
  category: SpecChangeCategory;
  reason: string;
  companionPath: string | null;
  companionChanged: boolean;
  linesAdded: number;
  linesRemoved: number;
  overrideCategory: SpecChangeCategory | null;
  overrideReason: string | null;
}

export interface RegressionRadarResult {
  overallStatus: SpecChangeCategory;
  intendedCount: number;
  atRiskCount: number;
  driftCount: number;
  totalSpecChanges: number;
  baselineSha: string | null;
  headSha: string | null;
  entries: SpecChangeEntry[];
  error: string | null;
}
