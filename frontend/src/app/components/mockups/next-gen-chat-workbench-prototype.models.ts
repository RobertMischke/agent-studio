export type WorkbenchPane = 'result' | 'git' | 'preview' | 'debug' | 'chat';
export type ContextPane = Exclude<WorkbenchPane, 'chat'>;
export type Density = 'comfortable' | 'compact';
export type Theme = 'light' | 'dark';
export type Scenario = 'review' | 'tools' | 'wait' | 'visual' | 'drift' | 'decisions';
export type DebugTab = 'overview' | 'actors' | 'tools' | 'tokens' | 'trace';
export type ComposeMode = 'continue' | 'extend' | 'steer' | 'followup';
export type ActivityTarget = 'projects' | 'tasks' | 'search' | 'git' | 'qa' | 'tokens';
export type StatusPanel = 'health' | 'queue' | 'tokens' | 'evidence' | 'model' | 'session' | 'projects';
export type ActorKind = 'user' | 'agent' | 'orchestrator' | 'supervisor' | 'support' | 'tool' | 'system';
export type InterventionTarget = 'currentRun' | 'nextRun' | 'orchestrator' | 'followUp';
export type DecisionKind = 'reissue' | 'heuristic' | 'needsInput' | 'circuit' | 'captureFail' | 'drift';
export type FeatureAction = 'prompt' | 'activity' | 'timeline' | 'git' | 'screenshots' | 'tokens' | 'sideSheet' | 'startStop';

export interface SummaryChip {
  label: string;
  value: string;
  icon: string;
  pane: WorkbenchPane;
  tone?: 'ok' | 'warn' | 'danger';
}

export interface ActorMeta {
  kind: ActorKind;
  label: string;
  glyph: string;
  icon: string;
  shape: 'circle' | 'rounded' | 'square' | 'hex' | 'shield' | 'triangle' | 'pill';
  help: string;
}

export interface ChatTurnEntry {
  kind: 'turn';
  id: string;
  actor: ActorKind;
  title: string;
  body: string;
  meta?: string;
  actions?: string[];
  intervention?: InterventionTarget;
}

export interface DecisionEntry {
  kind: 'decision';
  id: string;
  decision: DecisionKind;
  actor: ActorKind;
  title: string;
  summary: string;
  tone: 'info' | 'warn' | 'danger';
  reason: string;
  evidence: string;
  action: string;
  retry: string;
  tokens: string;
  traceRange: string;
  nextStep: string;
}

export type TranscriptEntry = ChatTurnEntry | DecisionEntry;

export interface ProjectTab {
  name: string;
  initial: string;
  active: boolean;
  auto: string;
  tooltip: string;
  color: string;
  soft: string;
  border: string;
  on: string;
}

export interface UsageStripItem {
  label: string;
  value: string;
  tone: 'ok' | 'warn' | 'hot';
  detail: string;
  window?: string;
  reset?: string;
  testId?: string;
}
