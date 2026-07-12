// Mirror of the C# DTO in update-service/Models.cs. Keep in sync; the wire
// shape is the contract between the standalone UpdateService (port 5039)
// and the FE banner/dev-tools surface.

export interface UpdateStatus {
  phase: UpdatePhase;
  phaseLabel: string | null;
  message: string | null;
  currentRunId: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  headLocal: string;
  headOrigin: string | null;
  behindBy: number;
  pendingCommits: CommitInfo[];
  lastFetchAt: string | null;
  lastUpdateAt: string | null;
  lastSuccessAt: string | null;
  // ADR-0031: when the most recent run finished. The FE shows the green
  // completion toast for `doneLingerSeconds` (default 60 s) after this
  // moment, even when the user joined the page while idle-polling.
  lastRunFinishedAt: string | null;
  lastRunHeadBefore: string | null;
  lastRunHeadAfter: string | null;
  isRunning: boolean;
  backendReachable: boolean;
  serviceVersion: string;
  productVersion: string;
  runningVersion: RuntimeVersion | null;
  mainVersion: BranchVersion | null;
  developVersion: BranchVersion | null;
  mode: 'manual' | 'scheduled';
  verificationFailures: VerificationFailure[] | null;
  autoRollbackEnabled: boolean;
}

export interface RuntimeVersion {
  version: string;
  commit: string;
  deployedAt: string | null;
  tag?: string | null;
  dirty?: boolean | null;
  identitySource?: 'release-manifest' | 'legacy-untagged' | string | null;
  manifest?: ReleaseManifest | null;
}

export interface ReleaseArtifact {
  name: string;
  version: string;
  tag: string | null;
  commit: string | null;
  integrity: string;
  source: string;
}

export interface ReleaseManifest {
  schemaVersion: number;
  appTag: string;
  appVersion: string;
  commit: string;
  dirty: boolean;
  builtAt: string;
  codingAgentRunner: ReleaseArtifact;
  codingAgentChat: ReleaseArtifact;
}

export interface BranchVersion {
  branch: 'main' | 'develop' | string;
  commit: string;
  commitAt: string | null;
  aheadBy: number;
  behindBy: number;
}

export interface CommitInfo {
  sha: string;
  subject: string;
  author: string;
  authorDate: string;
}

// ADR-0031: phase vocabulary widened from the original 4-step shell into
// the 9-phase pipeline. FE renders by `phaseLabel` first, falls back to
// `phase`. New phases are additive; tolerate unknown strings.
export type UpdatePhase =
  | 'idle'
  | 'preparing'
  | 'pausing-runners'
  | 'pulling'
  | 'building'
  | 'restarting'
  | 'verifying-after-restart'
  | 'resuming'
  | 'rolling-back'
  | 'done'
  | 'failed';

export interface VerificationFailure {
  step: string;
  observed: string | null;
  expected: string | null;
}

export interface UpdateHistoryEntry {
  runId: string;
  startedAt: string;
  finishedAt: string | null;
  status: 'ok' | 'failed' | 'aborted';
  headBefore: string;
  headAfter: string;
  durationSeconds: number;
  error: string | null;
  trigger: 'manual' | 'scheduled' | 'api';
  verificationFailures?: VerificationFailure[] | null;
  rollbackStatus?: 'ok' | 'failed' | null;
  runFolder?: string | null;
  releaseTag?: string | null;
  releaseCommit?: string | null;
  manifestIntegrity?: string | null;
}

export interface TriggerRequest {
  reason?: string;
  force?: boolean;
}

export interface TriggerResponse {
  runId: string;
  phase: UpdatePhase;
  message: string;
}

export interface RollbackRequest {
  runId: string;
}

export interface RollbackResponse {
  runId: string;
  phase: UpdatePhase;
  message: string;
}
