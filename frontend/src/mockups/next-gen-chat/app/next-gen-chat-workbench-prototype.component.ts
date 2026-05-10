import { Component, computed, signal } from '@angular/core';

import { FoundNextStatusbarComponent } from './found-next-statusbar.component';
import { FoundNextTopbarComponent } from './found-next-topbar.component';
import { NextGenChatActivityBarComponent } from './next-gen-chat-activity-bar.component';
import { NextGenChatContextDocumentComponent } from './next-gen-chat-context-document.component';
import { NextGenChatDocumentTabsComponent } from './next-gen-chat-document-tabs.component';
import { NextGenChatQueueComponent } from './next-gen-chat-queue.component';
import { NextGenChatRailComponent } from './next-gen-chat-rail.component';
import { ICON_PATHS, PROJECT_TABS, USAGE_STRIP } from './next-gen-chat-workbench-prototype.data';
import {
  ActivityTarget,
  ActivityItem,
  ActorKind,
  ActorMeta,
  ComposeMode,
  ContextPane,
  DebugTab,
  DecisionEntry,
  DecisionKind,
  Density,
  FeatureParityItem,
  FeatureAction,
  GitFileRow,
  InterventionTarget,
  PaneButton,
  Scenario,
  ScenarioOption,
  StatusPanel,
  SummaryChip,
  TaskQueueCard,
  Theme,
  TranscriptEntry,
  TokenUsageRow,
  WorkbenchDocument,
  WorkbenchDocumentId,
  WorkbenchPane,
} from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-next-gen-chat-workbench-prototype',
  standalone: true,
  imports: [
    FoundNextTopbarComponent,
    FoundNextStatusbarComponent,
    NextGenChatActivityBarComponent,
    NextGenChatContextDocumentComponent,
    NextGenChatDocumentTabsComponent,
    NextGenChatQueueComponent,
    NextGenChatRailComponent,
  ],
  templateUrl: './next-gen-chat-workbench-prototype.component.html'
})
export class NextGenChatWorkbenchPrototypeComponent {
  readonly closed = signal(false);
  readonly pane = signal<ContextPane>('result');
  readonly density = signal<Density>('comfortable');
  readonly theme = signal<Theme>('light');
  readonly activeScenario = signal<Scenario>('review');
  readonly toolOpen = signal(false);
  readonly sideSheetOpen = signal(true);
  readonly debugOpen = signal(false);
  readonly lightboxOpen = signal(false);
  readonly commandOpen = signal(false);
  readonly guideOpen = signal(false);
  readonly markerOpen = signal(false);
  readonly featureModal = signal<FeatureAction | null>(null);
  readonly statusPanel = signal<StatusPanel | null>(null);
  readonly activeActivity = signal<ActivityTarget>('tasks');
  readonly queueOpen = signal(true);
  readonly chatOpen = signal(true);
  readonly contextPanes = signal<readonly ContextPane[]>(['result']);
  readonly contextOpen = computed(() => this.contextPanes().length > 0);
  readonly activeDocument = signal<WorkbenchDocumentId>('result');
  readonly activeContextDocument = computed<ContextPane | null>(() => {
    const active = this.activeDocument();
    if (active === 'chat') return null;
    return active;
  });
  readonly activePaneIds = computed<readonly WorkbenchPane[]>(() => [
    ...(this.chatOpen() ? (['chat'] as const) : []),
    ...this.contextPanes(),
  ]);
  readonly splitRatio = signal(54);
  readonly splitDragging = signal(false);
  readonly activeGitFile = signal('frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.component.ts');
  readonly debugTab = signal<DebugTab>('overview');
  readonly composerMode = signal<ComposeMode>('continue');

  readonly projectTabs = PROJECT_TABS;
  readonly usageStrip = USAGE_STRIP;
  readonly iconPaths = ICON_PATHS;

  readonly activityItems: ActivityItem[] = [
    { id: 'projects', icon: 'folder', label: 'Projects', title: 'Projects and watched paths' },
    { id: 'tasks', icon: 'columns', label: 'Tasks', title: 'Task board and queue' },
    { id: 'search', icon: 'search', label: 'Search', title: 'Search chat and trace' },
    { id: 'git', icon: 'git', label: 'Git', title: 'Git changes' },
    { id: 'qa', icon: 'check', label: 'QA', title: 'QA, tests, and health' },
    { id: 'tokens', icon: 'tokens', label: 'Tokens', title: 'Token usage' },
  ];

  readonly paneButtons: PaneButton[] = [
    { id: 'chat', label: 'Open chat document', short: 'Chat', icon: 'chat' },
    { id: 'result', label: 'Open summary document', short: 'Summary', icon: 'check' },
    { id: 'git', label: 'Open Git document', short: 'Git', icon: 'git' },
    { id: 'preview', label: 'Open screenshot document', short: 'Preview', icon: 'image' },
    { id: 'debug', label: 'Open debug document', short: 'Debug', icon: 'bug' },
  ];

  readonly scenarios: ScenarioOption[] = [
    { id: 'review', label: 'Review', icon: 'check' },
    { id: 'tools', label: 'Tools', icon: 'terminal' },
    { id: 'wait', label: 'Wait', icon: 'clock' },
    { id: 'visual', label: 'Images', icon: 'image' },
    { id: 'drift', label: 'Drift', icon: 'warning' },
    { id: 'decisions', label: 'Decisions', icon: 'shield' },
  ];

  readonly actors: Record<ActorKind, ActorMeta> = {
    user: { kind: 'user', label: 'You', glyph: 'Y', icon: 'user', shape: 'pill', help: 'Human steering. Always right-aligned and target-tagged.' },
    agent: { kind: 'agent', label: 'Task Agent', glyph: 'A', icon: 'agent', shape: 'circle', help: 'The CLI working the active task.' },
    orchestrator: { kind: 'orchestrator', label: 'Orchestrator', glyph: 'O', icon: 'compass', shape: 'hex', help: 'Deterministic post-run policy. Reissues, heuristics, retry budget.' },
    supervisor: { kind: 'supervisor', label: 'Supervisor', glyph: 'S', icon: 'shield', shape: 'shield', help: 'Watchdog and circuit breaker. Quiet, resume, kill.' },
    support: { kind: 'support', label: 'Supporting Agent', glyph: 'Q', icon: 'helper', shape: 'rounded', help: 'Sub-agent or QA helper feeding a structured report back to the task.' },
    tool: { kind: 'tool', label: 'Tool Runner', glyph: 'T', icon: 'terminal', shape: 'square', help: 'Read, search, edit, shell, browser, and test invocations.' },
    system: { kind: 'system', label: 'System', glyph: '!', icon: 'warning', shape: 'triangle', help: 'Parser, capture, and contract warnings from the orchestrator runtime.' },
  };

  readonly actorRailItems: ActorKind[] = ['user', 'agent', 'orchestrator', 'supervisor', 'support', 'tool', 'system'];

  readonly interventionTargets: Record<InterventionTarget, { label: string; help: string; icon: string }> = {
    currentRun: { label: 'Current run', icon: 'play', help: 'Steers the run that is active right now.' },
    nextRun: { label: 'Next run', icon: 'clock', help: 'Lands as continuation context for the next CLI invocation.' },
    orchestrator: { label: 'Orchestrator', icon: 'compass', help: 'Talks to the deterministic post-run policy, not the agent body.' },
    followUp: { label: 'Follow-up task', icon: 'plus', help: 'Spawns a queued follow-up task instead of changing this run.' },
  };

  readonly decisionMeta: Record<DecisionKind, { label: string; actor: ActorKind; tone: 'info' | 'warn' | 'danger'; icon: string }> = {
    reissue: { label: 'Reissue', actor: 'orchestrator', tone: 'info', icon: 'rerun' },
    heuristic: { label: 'Heuristic outcome', actor: 'orchestrator', tone: 'warn', icon: 'warning' },
    needsInput: { label: 'Needs-input loop', actor: 'orchestrator', tone: 'warn', icon: 'help' },
    circuit: { label: 'Circuit breaker', actor: 'supervisor', tone: 'danger', icon: 'shield' },
    captureFail: { label: 'Capture fail', actor: 'system', tone: 'warn', icon: 'plug' },
    drift: { label: 'Schema drift', actor: 'system', tone: 'warn', icon: 'warning' },
  };

  readonly composeModes: Array<{ id: ComposeMode; label: string; icon: string; description: string }> = [
    { id: 'continue', label: 'Continue', icon: 'play', description: 'Continue the running task or restart the next run with this follow-up.' },
    { id: 'extend', label: 'Extend task', icon: 'file', description: 'Append a task extension to prompt history before the next run.' },
    { id: 'steer', label: 'Steer', icon: 'panel', description: 'Send steering to the orchestrator without changing the task body.' },
    { id: 'followup', label: 'Follow-up job', icon: 'plus', description: 'Create a queued follow-up task from this chat turn.' },
  ];

  readonly debugTabs: Array<{ id: DebugTab; label: string }> = [
    { id: 'overview', label: 'Overview' },
    { id: 'actors', label: 'Actors' },
    { id: 'tools', label: 'Tools' },
    { id: 'tokens', label: 'Tokens' },
    { id: 'trace', label: 'Trace' },
  ];

  readonly summaryChips: SummaryChip[] = [
    { value: 'Review', label: 'run 4', icon: 'check', pane: 'result', tone: 'ok' },
    { value: '42k', label: 'tokens', icon: 'tokens', pane: 'debug', tone: 'warn' },
    { value: '3', label: 'commits', icon: 'git', pane: 'git' },
    { value: '8', label: 'files', icon: 'fileDiff', pane: 'git' },
    { value: '4', label: 'images', icon: 'image', pane: 'preview' },
    { value: '1', label: 'retry fail', icon: 'warning', pane: 'debug', tone: 'danger' },
    { value: '12m', label: 'active', icon: 'clock', pane: 'result' },
  ];

  readonly taskCards: TaskQueueCard[] = [
    { id: 'bridge', title: 'Chat layout integration bridge', state: 'ready', lane: '2-ready', order: '50', agent: 'Codex', meta: '12m est', active: false },
    { id: 'projection', title: 'Next-gen chat conversation event projection', state: 'ready', lane: '2-ready', order: '60', agent: 'Codex', meta: 'active ref', active: true },
    { id: 'tools', title: 'Collapse tool-heavy chat logs into bursts', state: 'ready', lane: '2-ready', order: '70', agent: 'Claude', meta: 'QA linked', active: false },
    { id: 'actors', title: 'Chat actor rails and decision cards', state: 'ready', lane: '2-ready', order: '80', agent: 'Codex', meta: 'needs spec', active: false },
    { id: 'debug', title: 'Fullscreen verbose debug view', state: 'ready', lane: '2-ready', order: '90', agent: 'Codex', meta: 'debug view', active: false },
  ];

  readonly runMarkerDetails = [
    { label: 'CLI', value: 'Codex' },
    { label: 'Model', value: '5.5 Extra High' },
    { label: 'Mode', value: 'Continue' },
    { label: 'Session', value: 'preserved' },
    { label: 'Trace', value: 'lines 418-731' },
    { label: 'Outcome', value: 'review' },
    { label: 'Budget', value: '42k tokens' },
    { label: 'Artifacts', value: '4 screenshots' },
  ];

  readonly featureParity: FeatureParityItem[] = [
    { label: 'Prompt history', icon: 'file', note: 'Original prompt plus task extensions remain visible.', action: 'prompt' },
    { label: 'Activity and Trace', icon: 'terminal', note: 'Raw CLI output stays available through the debug lens.', action: 'activity' },
    { label: 'Run timeline', icon: 'clock', note: 'Run cards become thin chat markers with popover metadata.', action: 'timeline' },
    { label: 'Git review', icon: 'git', note: 'Files, commits, diff, commit message, and commit action remain accessible.', action: 'git' },
    { label: 'Screenshots', icon: 'image', note: 'Durable screenshot evidence opens in the preview pane.', action: 'screenshots' },
    { label: 'Token usage', icon: 'tokens', note: 'Task and project token pressure stay visible without crowding the transcript.', action: 'tokens' },
    { label: 'Side sheet', icon: 'panelOpen', note: 'Project-level steering remains in the resizable side sheet.', action: 'sideSheet' },
    { label: 'Start/Stop', icon: 'play', note: 'Execution controls move into the compact composer command deck.', action: 'startStop' },
  ];

  readonly transcript: TranscriptEntry[] = [
    {
      kind: 'turn',
      id: 'user-steer',
      actor: 'user',
      title: 'You',
      body: 'I want chat to be optional. Sometimes I need the transcript, sometimes I need Git, result, screenshots, or debug panes to own the workspace.',
      intervention: 'orchestrator',
    },
    {
      kind: 'turn',
      id: 'agent-1',
      actor: 'agent',
      title: 'Task Agent',
      body: 'The workbench treats Chat, Result, Git, Preview, and Debug as pinable panes. They can be combined without turning the task view into a full docking system.',
      actions: ['Show technical layer', 'Open Verbose Debug'],
    },
    {
      kind: 'decision',
      id: 'reissue-1',
      decision: 'reissue',
      actor: 'orchestrator',
      title: 'Orchestrator reissued the run',
      summary: 'Fast Done after follow-up. Reissued once with stronger framing.',
      tone: 'info',
      reason: 'Agent emitted [[TASK_DONE]] within 18s of a UserContinue carrying a follow-up. Policy treats this as suspect.',
      evidence: 'cli-output.log lines 412-431 plus prior follow-up at line 318.',
      action: 'Reissue with stronger framing once, then stop and ask the user.',
      retry: 'used 1 of 1 reissues',
      tokens: '+3.2k orchestrator tokens',
      traceRange: 'lines 318-431',
      nextStep: 'If next run also returns fast Done, the policy escalates to human review.',
    },
    {
      kind: 'turn',
      id: 'agent-2',
      actor: 'agent',
      title: 'Task Agent',
      body: 'Re-running with the stronger frame. The reissue turned a fast Done into a real implementation pass with three commits and four screenshots.',
      meta: 'run 4 active',
    },
    {
      kind: 'turn',
      id: 'support-1',
      actor: 'support',
      title: 'Design QA',
      meta: 'helper agent',
      body: 'Light mode is primary, dark mode matches hierarchy, mobile collapses to chat, and click interception is covered by Playwright.',
      actions: ['Open screenshots'],
    },
    {
      kind: 'turn',
      id: 'tool-1',
      actor: 'tool',
      title: 'Tool Runner',
      meta: '28 calls',
      body: 'read 12, search 7, edit 4, shell 3, browser 2. One shell failure on playwright chromium retried successfully.',
      actions: ['Show technical layer'],
    },
  ];

  readonly waitDecision: DecisionEntry = {
    kind: 'decision',
    id: 'circuit-1',
    decision: 'circuit',
    actor: 'supervisor',
    title: 'Supervisor watched a quiet window',
    summary: '30s silent, agent resumed. No kill issued.',
    tone: 'warn',
    reason: 'Agent stdout went quiet at line 612 and resumed at line 614 without producing structured output.',
    evidence: 'last output was a tool spawn header; resume produced an answer 30s later.',
    action: 'Hold the kill switch. Emit advisory only.',
    retry: 'within 1/3 quiet windows for this run',
    tokens: 'no orchestrator tokens spent',
    traceRange: 'lines 612-679',
    nextStep: 'A second quiet window above 90s would trip the circuit breaker.',
  };

  readonly driftDecision: DecisionEntry = {
    kind: 'decision',
    id: 'drift-1',
    decision: 'drift',
    actor: 'system',
    title: 'Schema drift in structured report',
    summary: 'Report does not match the JSON contract. Markdown body still renders.',
    tone: 'warn',
    reason: 'Expected `{ summary, evidence, nextStep }` but the agent emitted free-form Markdown headings.',
    evidence: 'parser warning at line 731. Raw Markdown remains attached.',
    action: 'Surface as a system row. Keep the Markdown human-readable. Flag drift in metrics.',
    retry: 'no retry consumed',
    tokens: 'no extra orchestrator tokens',
    traceRange: 'lines 715-742',
    nextStep: 'If drift recurs in the next run, queue a contract follow-up task.',
  };

  readonly decisionShowcase: TranscriptEntry[] = [
    {
      kind: 'turn',
      id: 'user-currentRun',
      actor: 'user',
      title: 'You',
      body: 'Stop the active run. The Playwright shell call is in a retry loop and will burn tokens.',
      intervention: 'currentRun',
    },
    {
      kind: 'decision',
      id: 'heuristic-1',
      decision: 'heuristic',
      actor: 'orchestrator',
      title: 'Outcome inferred without a sentinel',
      summary: 'Could not classify the agent reply. Fell back to heuristic.',
      tone: 'warn',
      reason: 'No hard sentinel matched. Last 60 lines suggest "needs review", confidence 0.52.',
      evidence: 'matched phrase "ready for human review" at line 504; no [[TASK_*]] sentinel in the log.',
      action: 'Mark MatchedSentinel = false. Surface heuristic verdict as a meta message.',
      retry: 'retry budget unchanged',
      tokens: '+0.4k parser tokens',
      traceRange: 'lines 446-505',
      nextStep: 'Recommend the agent emit [[TASK_DONE]] explicitly on the next pass.',
    },
    {
      kind: 'decision',
      id: 'needsInput-1',
      decision: 'needsInput',
      actor: 'orchestrator',
      title: 'Needs-input loop counter advanced',
      summary: 'Third needs-input in a row. One slot left before circuit trip.',
      tone: 'warn',
      reason: 'Agent asked the same disambiguation three times. Loop guard threshold is 4.',
      evidence: 'sentinels [[TASK_NEEDS_INPUT:scope]] at lines 220, 318, 401.',
      action: 'Answer with the most recent project rule, mark loop counter at 3/4.',
      retry: 'loop 3 of 4',
      tokens: '+1.1k orchestrator tokens',
      traceRange: 'lines 220-401',
      nextStep: 'A fourth identical question hands off to the user.',
    },
    {
      kind: 'turn',
      id: 'user-followUp',
      actor: 'user',
      title: 'You',
      body: 'Do not change this task body. Queue a follow-up that fixes the Playwright shell flake.',
      intervention: 'followUp',
    },
    {
      kind: 'decision',
      id: 'circuit-showcase',
      decision: 'circuit',
      actor: 'supervisor',
      title: 'Supervisor armed the circuit breaker',
      summary: 'Two quiet windows in this run. Next breach trips kill.',
      tone: 'danger',
      reason: 'Quiet windows of 30s and 65s back-to-back without structured output.',
      evidence: 'watchdog markers at lines 612 and 740. No tool calls in between.',
      action: 'Hold kill. Raise the breaker to "armed". Next quiet > 90s ends the run.',
      retry: '2 of 3 quiet windows used',
      tokens: 'no orchestrator tokens spent',
      traceRange: 'lines 612-742',
      nextStep: 'Operator can pre-empt with Pause to keep tokens out of a kill cycle.',
    },
    {
      kind: 'decision',
      id: 'captureFail-1',
      decision: 'captureFail',
      actor: 'system',
      title: 'Session capture failed',
      summary: 'No Claude session id from this run. Next continuation rebuilds from disk.',
      tone: 'warn',
      reason: 'CLI exited before the session sentinel landed in `~/.claude/projects/.../session.jsonl`.',
      evidence: '[capture-fail] log marker at line 802. Session registry sees no id.',
      action: 'Mark session as rebuilt-on-next-continue. Keep the run output intact.',
      retry: 'no retry consumed',
      tokens: 'no orchestrator tokens spent',
      traceRange: 'lines 798-815',
      nextStep: 'Next continuation re-derives prompt history and attaches the original task body.',
    },
    {
      kind: 'turn',
      id: 'user-nextRun',
      actor: 'user',
      title: 'You',
      body: 'Before you start the next run, switch the model to Haiku 4.5 to keep the budget tight.',
      intervention: 'nextRun',
    },
    {
      kind: 'decision',
      id: 'drift-showcase',
      decision: 'drift',
      actor: 'system',
      title: 'Schema drift in structured report',
      summary: 'Report does not match the JSON contract. Markdown body still renders.',
      tone: 'warn',
      reason: 'Expected `{ summary, evidence, nextStep }` but the agent emitted free-form Markdown headings.',
      evidence: 'parser warning at line 731. Raw Markdown remains attached.',
      action: 'Surface as a system row. Keep the Markdown human-readable. Flag drift in metrics.',
      retry: 'no retry consumed',
      tokens: 'no extra orchestrator tokens',
      traceRange: 'lines 715-742',
      nextStep: 'If drift recurs in the next run, queue a contract follow-up task.',
    },
  ];

  readonly toolRows = [
    { tool: 'read', target: 'activity-log.parser.ts', result: 'ok', tone: 'ok' },
    { tool: 'search', target: '136 cli-output.log fixtures', result: 'ok', tone: 'ok' },
    { tool: 'shell', target: 'playwright chromium', result: 'failed once', tone: 'danger' },
    { tool: 'browser', target: 'v7 workbench screenshots', result: 'passed', tone: 'ok' },
  ];

  readonly gitFiles: GitFileRow[] = [
    { path: 'frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.component.ts', delta: '+812 -0' },
    { path: 'frontend/src/mockups/next-gen-chat/app/next-gen-chat-context-document.component.ts', delta: '+214 -0' },
    { path: 'frontend/src/app/app.ts', delta: '+3 -0' },
    { path: 'frontend/src/app/services/feature-flags.service.ts', delta: '+8 -0' },
    { path: 'docs/mockups/chat-window-next-gen/README.md', delta: '+9 -1' },
    { path: 'frontend/e2e/next-gen-chat-angular-prototype.spec.ts', delta: '+82 -0' },
  ];

  readonly screenshots = ['Result split', 'Git split', 'Compact mode', 'Debug modal'];

  readonly tokenRows: TokenUsageRow[] = [
    { name: 'Agent', value: '28k', percent: 82 },
    { name: 'Orch.', value: '9k', percent: 34 },
    { name: 'Support', value: '5k', percent: 18 },
  ];

  readonly debugBands = [
    { name: 'Tool density', value: '28 calls', percent: 78 },
    { name: 'Tokens', value: '42k', percent: 64 },
    { name: 'Wait loop', value: '30s', percent: 38 },
    { name: 'Images', value: '4 files', percent: 52 },
  ];

  readonly visibleTurns = computed<TranscriptEntry[]>(() => {
    const scenario = this.activeScenario();
    if (scenario === 'tools') return this.transcript;
    if (scenario === 'wait') {
      return [...this.transcript, this.waitDecision];
    }
    if (scenario === 'visual') {
      return this.transcript.map((entry) =>
        entry.kind === 'turn' && entry.actor === 'support'
          ? { ...entry, body: 'Screenshots are rendered as a compact evidence reel and open into a durable lightbox. Scratch output is never the only evidence path.' }
          : entry
      );
    }
    if (scenario === 'drift') {
      return [...this.transcript, this.driftDecision];
    }
    if (scenario === 'decisions') {
      return [...this.transcript, ...this.decisionShowcase];
    }
    return this.transcript;
  });

  readonly scenarioText = computed(() => {
    switch (this.activeScenario()) {
      case 'tools':
        return 'Tool-heavy logs collapse into one readable row by default. Expand for exact commands and raw trace ranges.';
      case 'wait':
        return 'Watchdog quiet, resume, and kill events become low-noise supervisor rows with timing detail.';
      case 'visual':
        return 'Visual evidence gets a preview pane and lightbox without turning the transcript into a gallery.';
      case 'drift':
        return 'Parser drift, duplicate sentinels, and malformed reports stay visible but human-first.';
      case 'decisions':
        return 'Reissue, heuristic, needs-input, circuit, capture-fail, and drift become one-line rows with full causal detail on expand.';
      default:
        return 'Review mode lets chat, result, Git, screenshots, and debug panes be pinned only when useful.';
    }
  });

  readonly actorRailCounts = computed<Record<ActorKind, number>>(() => {
    const counts: Record<ActorKind, number> = {
      user: 0, agent: 0, orchestrator: 0, supervisor: 0, support: 0, tool: 0, system: 0,
    };
    for (const entry of this.visibleTurns()) {
      counts[entry.actor] += 1;
    }
    return counts;
  });

  readonly expandedDecisions = signal<ReadonlySet<string>>(new Set());

  isDecisionExpanded(id: string): boolean {
    return this.expandedDecisions().has(id);
  }

  toggleDecision(id: string): void {
    const next = new Set(this.expandedDecisions());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.expandedDecisions.set(next);
  }

  actorMeta(kind: ActorKind): ActorMeta {
    return this.actors[kind];
  }

  interventionMeta(target: InterventionTarget) {
    return this.interventionTargets[target];
  }

  readonly openDocuments = computed<WorkbenchDocument[]>(() => {
    const docs: WorkbenchDocument[] = [];
    if (this.contextPanes().includes('result')) {
      docs.push({ id: 'result', title: 'Summary', subtitle: 'default dashboard', icon: 'check', closable: false });
    }
    if (this.chatOpen()) {
      docs.push({ id: 'chat', title: 'Task Chat', subtitle: 'conversation', icon: 'chat', closable: true });
    }
    for (const pane of this.contextPanes()) {
      if (pane === 'result') continue;
      docs.push({
        id: pane,
        title: this.documentTitle(pane),
        subtitle: this.documentSubtitle(pane),
        icon: this.documentIcon(pane),
        closable: true,
      });
    }
    if (docs.length === 0) {
      docs.push({ id: 'result', title: 'Summary', subtitle: 'default dashboard', icon: 'check', closable: false });
    }
    return docs;
  });

  readonly workbenchColumns = computed(() => {
    const columns: string[] = [];
    const panes = this.contextPanes();
    const manyPanes = panes.length > 1;
    if (this.chatOpen()) {
      const chatMinimum = manyPanes ? 300 : 320;
      const chatShare = manyPanes ? Math.min(this.splitRatio(), 48) : this.splitRatio();
      columns.push(`minmax(${chatMinimum}px, ${chatShare}%)`);
      if (panes.length > 0) columns.push('7px');
    }
    for (const pane of panes) {
      if (manyPanes) {
        columns.push(pane === 'git' ? 'minmax(360px, 1.25fr)' : 'minmax(220px, .85fr)');
      } else {
        columns.push(pane === 'git' ? 'minmax(430px, 1.25fr)' : 'minmax(255px, .85fr)');
      }
    }
    return columns.length ? columns.join(' ') : 'minmax(0, 1fr)';
  });

  readonly composerModeLabel = computed(() => {
    switch (this.composerMode()) {
      case 'extend': return 'Extend';
      case 'steer': return 'Steer';
      case 'followup': return 'Create job';
      default: return 'Continue';
    }
  });

  setPane(pane: WorkbenchPane): void {
    if (pane === 'chat') {
      this.chatOpen.set(true);
      this.activeDocument.set('chat');
      return;
    }
    this.pane.set(pane);
    this.activeDocument.set(pane);
    this.addContextPane(pane);
  }

  isPaneButtonActive(pane: WorkbenchPane): boolean {
    if (pane === 'chat') return this.chatOpen();
    return this.contextPanes().includes(pane);
  }

  togglePane(pane: WorkbenchPane): void {
    this.setPane(pane);
  }

  toggleChat(): void {
    if (this.chatOpen() && this.contextPanes().length === 0) {
      this.addContextPane('result');
    }
    const wasActiveChat = this.activeDocument() === 'chat';
    const nextOpen = !this.chatOpen();
    this.chatOpen.set(nextOpen);
    if (nextOpen) {
      this.activeDocument.set('chat');
    } else if (wasActiveChat) {
      this.activeDocument.set(this.contextPanes()[0] ?? 'result');
    }
  }

  closeContextPane(pane: ContextPane): void {
    this.removeContextPane(pane);
  }

  openAllContextPanes(): void {
    this.contextPanes.set(['result', 'git', 'preview', 'debug']);
    this.pane.set('result');
    this.activeDocument.set('result');
    this.sideSheetOpen.set(false);
  }

  activateDocument(id: WorkbenchDocumentId): void {
    this.setPane(id);
  }

  closeDocument(id: WorkbenchDocumentId): void {
    if (id === 'chat') {
      const wasActiveChat = this.activeDocument() === 'chat';
      this.chatOpen.set(false);
      if (wasActiveChat) {
        this.activeDocument.set(this.contextPanes()[0] ?? 'result');
      }
      return;
    }
    if (id === 'result') return;
    this.removeContextPane(id);
  }

  documentTitle(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'Task Chat';
      case 'git': return 'Git changes';
      case 'preview': return 'Screenshots';
      case 'debug': return 'Debug trace';
      default: return 'Summary';
    }
  }

  documentSubtitle(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'conversation';
      case 'git': return 'source diff';
      case 'preview': return 'visual evidence';
      case 'debug': return 'diagnostics';
      default: return 'default dashboard';
    }
  }

  documentIcon(pane: WorkbenchPane): string {
    switch (pane) {
      case 'chat': return 'chat';
      case 'git': return 'git';
      case 'preview': return 'image';
      case 'debug': return 'bug';
      default: return 'check';
    }
  }

  private addContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => {
      if (panes.includes(pane)) return panes;
      const next = [...panes, pane];
      if (next.length > 1) this.sideSheetOpen.set(false);
      return next;
    });
  }

  private removeContextPane(pane: ContextPane): void {
    this.contextPanes.update((panes) => panes.filter((openPane) => openPane !== pane));
    if (this.activeDocument() === pane) {
      this.activeDocument.set(this.chatOpen() ? 'chat' : (this.contextPanes()[0] ?? 'result'));
    }
    if (!this.chatOpen() && this.contextPanes().length === 0) {
      this.chatOpen.set(true);
      this.activeDocument.set('chat');
    }
  }

  setSplitRatioValue(value: number): void {
    this.splitRatio.set(Math.max(34, Math.min(72, Math.round(value))));
  }

  startSplitResize(event: PointerEvent): void {
    if (!this.chatOpen() || !this.contextOpen()) return;

    const host = (event.currentTarget as HTMLElement).parentElement;
    if (!host) return;

    event.preventDefault();
    this.splitDragging.set(true);

    const updateFromPointer = (rawEvent: Event) => {
      const pointer = rawEvent as PointerEvent;
      const rect = host.getBoundingClientRect();
      if (rect.width <= 0) return;
      const next = ((pointer.clientX - rect.left) / rect.width) * 100;
      this.setSplitRatioValue(next);
    };

    const stopResize = () => {
      this.splitDragging.set(false);
      document.removeEventListener('pointermove', updateFromPointer);
    };

    updateFromPointer(event);
    document.addEventListener('pointermove', updateFromPointer);
    document.addEventListener('pointerup', stopResize, { once: true });
  }

  resizeSplitFromKeyboard(event: KeyboardEvent): void {
    const step = event.shiftKey ? 10 : 4;
    let next: number | null = null;

    if (event.key === 'ArrowLeft') next = this.splitRatio() - step;
    if (event.key === 'ArrowRight') next = this.splitRatio() + step;
    if (event.key === 'Home') next = 34;
    if (event.key === 'End') next = 72;

    if (next === null) return;
    event.preventDefault();
    this.setSplitRatioValue(next);
  }

  handleActivity(target: ActivityTarget): void {
    this.activeActivity.set(target);
    if (target === 'git') this.setPane('git');
    if (target === 'qa') this.toggleStatusPanel('health');
    if (target === 'tokens') this.toggleStatusPanel('tokens');
    if (target === 'tasks') this.toggleQueueModule();
    if (target === 'search') this.commandOpen.set(true);
    if (target === 'projects') this.sideSheetOpen.set(true);
  }

  openQueueModule(): void {
    this.queueOpen.set(true);
    this.activeActivity.set('tasks');
  }

  closeQueueModule(): void {
    this.queueOpen.set(false);
    if (this.activeActivity() === 'tasks') {
      this.activeActivity.set('projects');
    }
  }

  toggleQueueModule(): void {
    if (this.queueOpen()) this.closeQueueModule();
    else this.openQueueModule();
  }

  openFeatureParity(action: FeatureAction): void {
    switch (action) {
      case 'activity':
        this.debugTab.set('trace');
        this.debugOpen.set(true);
        return;
      case 'timeline':
        this.featureModal.set('timeline');
        return;
      case 'git':
        this.setPane('git');
        return;
      case 'screenshots':
        this.setPane('preview');
        return;
      case 'tokens':
        this.debugTab.set('tokens');
        this.debugOpen.set(true);
        return;
      case 'sideSheet':
        this.sideSheetOpen.set(true);
        return;
      case 'startStop':
        this.featureModal.set('startStop');
        return;
      default:
        this.featureModal.set('prompt');
    }
  }

  featureTitle(feature: FeatureAction): string {
    switch (feature) {
      case 'timeline': return 'Run timeline';
      case 'startStop': return 'Start and stop controls';
      default: return 'Prompt history';
    }
  }

  toggleStatusPanel(panel: StatusPanel): void {
    this.statusPanel.set(this.statusPanel() === panel ? null : panel);
  }

  statusPanelTitle(): string {
    switch (this.statusPanel()) {
      case 'queue': return 'Queue and automation';
      case 'tokens': return 'CLI usage and tokens';
      case 'evidence': return 'Visual evidence';
      case 'session': return 'Session continuity';
      case 'projects': return 'Project and owner filters';
      case 'model': return 'CLI and model controls';
      default: return 'System health';
    }
  }

  toggleDensity(): void {
    this.density.set(this.density() === 'compact' ? 'comfortable' : 'compact');
  }

  toggleTheme(): void {
    this.theme.set(this.theme() === 'light' ? 'dark' : 'light');
  }

  handleAction(action: string): void {
    if (action === 'Open Verbose Debug' || action === 'Debug pane') this.debugOpen.set(true);
    if (action === 'Git split') this.setPane('git');
    if (action === 'Open screenshots') this.setPane('preview');
    if (action === 'Open changes') this.setPane('git');
    if (action === 'Show technical layer') this.toolOpen.set(true);
  }

  openTrace(_range: string): void {
    this.debugTab.set('trace');
    this.debugOpen.set(true);
  }

  iconPath(name: string): string[] {
    return this.iconPaths[name] ?? this.iconPaths['panel'];
  }

  close(): void {
    this.closed.set(true);
  }
}
