// Mirror of the C# DTO in update-service/Models.cs. Keep in sync; the wire
// shape is the contract between the standalone UpdateService (port 5039)
// and the FE banner/dev-tools surface.

export interface UpdateStatus {
  phase: UpdatePhase;
  message: string | null;
  currentRunId: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  headLocal: string;
  headOrigin: string | null;
  behindBy: number;
  lastFetchAt: string | null;
  lastUpdateAt: string | null;
  lastSuccessAt: string | null;
  isRunning: boolean;
  backendReachable: boolean;
  version: string;
  mode: 'manual' | 'scheduled';
}

export type UpdatePhase =
  | 'idle'
  | 'preparing'
  | 'pausing-runners'
  | 'pulling'
  | 'building'
  | 'restarting'
  | 'resuming'
  | 'done'
  | 'failed';

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
