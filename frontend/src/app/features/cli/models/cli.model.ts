/**
 * Cycle 9 CLI catalog + usage models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Covers the model catalog returned by `/api/cli/{type}/models` and the
 * cross-CLI usage report from `/api/cli/usage`. Job-coupled CLI types
 * (CliExecution, CliSettings, ContinueMode, ContextUsageSnapshot) stay
 * in job.model.ts because they participate in the JobInfo graph.
 */

import type { CliType, SessionUsage } from '../../../models/job.model';

export interface CliModelInfo {
  id: string;
  label: string;
  multiplier: number | null;
  vendor: string | null;
  isDefault: boolean;
}

export interface CliModelCatalog {
  models: CliModelInfo[];
  source: string;
  fetchedAt?: string;
}

// Backwards-compat aliases — the records were Copilot-named before the multi-CLI refactor.
export type CopilotModelInfo = CliModelInfo;
export type CopilotModelCatalog = CliModelCatalog;

export interface CliSessionInfo {
  id: string;
  label: string | null;
  updatedAt: string | null;
  cwd: string | null;
  lastUsage: SessionUsage | null;
  isProjectDefault: boolean;
}

export interface CliUsageProjectGroup {
  projectName: string;
  rootPath: string | null;
  sessions: CliSessionInfo[];
}

export interface CliUsageSection {
  cliType: CliType;
  available: boolean;
  version: string | null;
  error: string | null;
  projects: CliUsageProjectGroup[];
}

export interface CliUsageReport {
  at: string;
  sections: CliUsageSection[];
}
