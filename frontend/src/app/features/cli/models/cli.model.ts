/**
 * Cycle 9 CLI catalog + usage models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Covers the model catalog returned by `/api/cli/{type}/models` and the
 * cross-CLI usage report from `/api/cli/usage`. Job-coupled CLI types
 * (CliExecution, CliSettings, ContinueMode, ContextUsageSnapshot) stay
 * in job.model.ts because they participate in the TaskInfo graph.
 */

import type { CliType, SessionUsage } from '../../../models/task.model';

export interface CliModelInfo {
  id: string;
  label: string;
  multiplier: number | null;
  vendor: string | null;
  isDefault: boolean;
  thinkingLevels?: string[];
  defaultThinkingLevel?: string | null;
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
  /**
   * Back-reference to the kanban task that owns this session, when the
   * session id appears in any job's `sessionChain`. Null for orphan
   * sessions (ad-hoc CLI use, sessions from another checkout). Drives
   * the small chip rendered next to the session row.
   */
  linkedJob: LinkedJobRef | null;
}

/**
 * Mirror of the backend `LinkedJobRef` record (see backend/Models/CliTypes.cs).
 * `lane` is the on-disk state slug; `isActive` is true when the owning job is
 * in `3-progress` AND the runner reports it as the project's currently-running
 * task. The frontend reads both to choose the chip colour rule.
 */
export interface LinkedJobRef {
  jobId: string;
  title: string;
  watchPath: string;
  projectName: string;
  lane: string;
  isActive: boolean;
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
