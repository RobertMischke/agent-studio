// Mirrors backend/Services/Supervisor/SupervisorContract.cs.
// Kept narrow on purpose - the supervisor surface is read-only on the
// frontend in this first cut, plus the four manual emergency primitives.

export type SupervisorSeverity = 'Info' | 'Warn' | 'High';
export type SupervisorSource = 'HardCheck' | 'SoftReasoning' | 'User' | 'AutoIntervention';
export type SupervisorInterventionKind = 'CancelRun' | 'PausePickup' | 'ForceFail' | 'Resume';

export interface SupervisorQuotaWindow {
  cli: string;
  usedFraction: number;
  resetAt: string | null;
}

export interface SupervisorRecentDecision {
  at: string;
  kind: string;
  summary: string;
}

export interface SupervisorErrorCounts {
  cliErrorsLastHour: number;
  orchestratorErrorsLastHour: number;
  runFailuresLastHour: number;
}

export interface SupervisorObservation {
  capturedAt: string;
  project: string;
  runnerStatus: string;
  currentJobId: string | null;
  currentRunState: string | null;
  lastProgressAt: string | null;
  quota: SupervisorQuotaWindow | null;
  recentDecisions: SupervisorRecentDecision[];
  recentAgentSamples: string[];
  errorCounts: SupervisorErrorCounts;
}

export interface SupervisorAdvisory {
  createdAt: string;
  project: string;
  severity: SupervisorSeverity;
  source: SupervisorSource;
  topic: string;
  message: string;
  jobId: string | null;
}

export interface SupervisorIntervention {
  createdAt: string;
  project: string;
  kind: SupervisorInterventionKind;
  source: SupervisorSource;
  reason: string;
  jobId: string | null;
  pauseTtl: string | null;
}

export interface SupervisorRecentEvents {
  advisories: SupervisorAdvisory[];
  interventions: SupervisorIntervention[];
}
