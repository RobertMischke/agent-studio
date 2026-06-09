/**
 * Pure projection scaffold for the next-gen chat (`Frontend:NextGenChat`).
 *
 * Walks raw `CliOutputLine[]` plus optional run / token / screenshot / commit
 * / job context and yields a sequence of `ConversationEvent`s. The function
 * MUST stay free of Angular services and DOM state so it can be unit-tested
 * deterministically against fixture log fragments.
 *
 * The classification rules here mirror the v6 edge-case taxonomy in
 * `docs/mockups/chat-window-next-gen/activity-log-edge-cases.md` and the
 * v7 workbench events listed in
 * `docs/mockups/chat-window-next-gen/integration-plan.md`. Renderers
 * should not pattern-match raw lines themselves; they should consume
 * `ConversationEvent[]`.
 */

import type { CliOutputLine, TaskInfo } from '../../models/task.model';
import type { GitFileChange } from '../../features/git';
import type { TaskTokenSummary } from '../../features/tokens';
import type { RunRecord, RunTimeline } from '../../features/run-timeline';
import {
  parseActivityLog,
  type ActivityLogGroup,
  type ActivityLogKind,
} from '../../features/task-detail';
import type {
  ConversationEvent,
  RawLineRange,
  ToolCommandExecution,
  ToolOutputHit,
  ToolFamily,
  TraceLink,
  WorkbenchSummaryAggregate
} from './conversation-event';

export interface ScreenshotEvidence {
  /** Caption / alt text. */
  caption: string;
  /** Original (often scratch) path. */
  sourcePath: string;
  /** Durable copy under `results/` after curation, when known. */
  durablePath?: string | null;
  sourceTool?: string;
  /** Optional ISO timestamp the host already attached. */
  timestamp?: string;
  taskLink?: string;
}

export interface CommitEvidence {
  sha: string;
  shortSha: string;
  subject: string;
  authorDateUtc: string;
  files: readonly GitFileChange[];
  runIndex?: number;
}

export interface ConversationProjectionContext {
  /** Source identifier kept on every `RawLineRange` (job id is preferred). */
  source: string;
  /** Raw activity log lines. The projection numbers them 1-based for ranges. */
  lines: readonly CliOutputLine[];
  job?: TaskInfo | null;
  runTimeline?: RunTimeline | null;
  tokenSummary?: TaskTokenSummary | null;
  screenshots?: readonly ScreenshotEvidence[];
  commits?: readonly CommitEvidence[];
  /** When true, runs are emitted as `runMarker` events even if the timeline is empty. */
  emitRunMarkers?: boolean;
  /** When true, the projection appends a `workbench.summary` event for the whole transcript. */
  emitWorkbenchSummary?: boolean;
  /** When true, the projection appends a `workbench.gitPreview` and `workbench.visualPreview` event when evidence exists. */
  emitWorkbenchPreviews?: boolean;
  /** When true, a final `traceLink` event is appended pointing at the raw log. */
  emitTraceLink?: boolean;
  /** When true, a `workbench.debug` aggregate is appended for the Verbose Debug pane. */
  emitDebugAggregate?: boolean;
  /** Latest run result string the host knows about (e.g. `[[TASK_DONE]]`, `heuristic-noop`). */
  latestResult?: string;
}

/** Public entry point — returns a flat, ordered list of conversation events. */
export function projectConversation(
  ctx: ConversationProjectionContext
): ConversationEvent[] {
  const events: ConversationEvent[] = [];
  const lineNumbers = numberLines(ctx.lines);
  const groups = parseActivityLog([...ctx.lines]);
  // Map activity-log groups back to their 1-based source line ranges so each
  // emitted event keeps a faithful raw range. The parser preserves line
  // identity through merges, so the lookup-by-reference is safe.
  const indexByLine = new Map<CliOutputLine, number>();
  ctx.lines.forEach((l, i) => indexByLine.set(l, i + 1));

  let currentRun: RunContext = pickInitialRun(ctx.runTimeline ?? null);
  const runByLineIndex = buildRunIndex(ctx.lines, ctx.runTimeline ?? null);
  const seenParserDedupeKeys = new Set<string>();

  for (let i = 0; i < groups.length; i++) {
    const group = groups[i];
    const range = rangeForGroup(group, indexByLine, ctx.source);
    const startLineIdx = range.start;
    const matchedRun = runByLineIndex.get(startLineIdx);
    if (matchedRun?.run && matchedRun.run.index !== currentRun?.run?.index) {
      currentRun = matchedRun;
      if (ctx.emitRunMarkers) {
        events.push(toRunMarker(matchedRun, range));
      }
    }

    // Contiguous tool / failed-tool groups collapse into a single ToolBurst
    // event so the chat does not paint a wall of chips. The window stops at
    // the first non-tool group; user / agent / orchestrator turns always
    // break a burst even when the agent immediately resumes tool calls
    // afterwards (the natural reading rhythm is tool burst → reply).
    const burstFamily = classifyToolGroup(group);
    if (burstFamily) {
      const burstGroups: { group: ActivityLogGroup; family: ToolFamily }[] = [
        { group, family: burstFamily }
      ];
      let lookahead = i + 1;
      while (lookahead < groups.length) {
        const next = groups[lookahead];
        const nextFamily = classifyToolGroup(next);
        if (!nextFamily) break;
        burstGroups.push({ group: next, family: nextFamily });
        lookahead += 1;
      }
      const lastIdx = lookahead - 1;
      const lastRange = rangeForGroup(groups[lastIdx], indexByLine, ctx.source);
      const mergedRange: RawLineRange = {
        source: ctx.source,
        start: range.start,
        end: Math.max(range.end, lastRange.end)
      };
      events.push(toMergedToolBurst(burstGroups, mergedRange, currentRun?.run?.index));
      i = lastIdx;
      continue;
    }

    const ev = projectGroup(group, range, currentRun, seenParserDedupeKeys);
    if (ev) events.push(...ev);
  }

  // Image artefacts and token metrics come from companion sources, not the
  // activity log itself, so they are appended after the line walk. They keep
  // a synthetic raw range that points at the start of the transcript so the
  // renderer can still link back to context.
  if (ctx.screenshots) {
    for (const shot of ctx.screenshots) events.push(toImageEvent(shot, ctx, lineNumbers));
  }
  if (ctx.tokenSummary) {
    events.push(toTaskTokenMetric(ctx.tokenSummary, ctx, lineNumbers));
  }
  if (currentRun?.run && ctx.tokenSummary) {
    // No-op placeholder: per-run token metrics get split out by a later job
    // when run-level token attribution lands in the backend response.
  }
  if (ctx.commits && ctx.commits.length > 0 && ctx.emitWorkbenchPreviews) {
    events.push(toGitPreview(ctx.commits, ctx, lineNumbers));
  }
  if (ctx.screenshots && ctx.screenshots.length > 0 && ctx.emitWorkbenchPreviews) {
    events.push(toVisualPreview(ctx.screenshots, ctx, lineNumbers));
  }
  if (ctx.job && ctx.emitRunMarkers) {
    events.push(toTaskMarker(ctx.job, ctx, lineNumbers));
  }
  if (ctx.emitWorkbenchSummary) {
    events.push(toWorkbenchSummary(events, ctx, lineNumbers));
  }
  if (ctx.emitDebugAggregate) {
    events.push(toWorkbenchDebug(events, ctx, lineNumbers));
  }
  if (ctx.emitTraceLink) {
    events.push(toTraceLink(ctx, lineNumbers));
  }
  return events;
}

// ──────────────────────────────────────────────────────────────────────────
// Group → event projection
// ──────────────────────────────────────────────────────────────────────────

function projectGroup(
  group: ActivityLogGroup,
  range: RawLineRange,
  currentRun: RunContext,
  seenParserDedupeKeys: Set<string>
): ConversationEvent[] | null {
  const firstLine = group.lines[0];
  if (!firstLine) return null;
  const ts = firstLine.timestamp;
  const baseId = `${range.source}:${range.start}-${range.end}`;
  const runId = currentRun?.run?.index;

  // User messages are always their own turn.
  if (firstLine.stream === 'user') {
    return [
      {
        id: `${baseId}:user`,
        kind: 'message.user',
        timestamp: ts,
        runId,
        rawRange: range,
        actor: 'You',
        body: group.title,
        target: extractUserTarget(firstLine.text)
      }
    ];
  }

  if (firstLine.stream === 'orchestrator') {
    // [watchdog] orchestrator messages get classified as supervisor.wait so
    // the chat row uses the correct family. The parser already filters them
    // out of conversation mode but the projection is the single source of
    // truth here, so it must classify on its own.
    if (/\[watchdog[^\]]*\]/i.test(firstLine.text)) {
      const wait = parseWatchdogText(firstLine.text);
      if (wait) {
        return [
          {
            id: `${baseId}:wait`,
            kind: 'supervisor.wait',
            timestamp: ts,
            runId,
            rawRange: range,
            severity: wait.state === 'killed' ? 'error' : wait.state === 'quiet' ? 'warn' : 'info',
            state: wait.state,
            quietSeconds: wait.quietSeconds,
            reason: wait.reason
          }
        ];
      }
    }

    const status = parseOrchestratorStatus(firstLine.text);
    if (status) {
      return [
        {
          id: `${baseId}:status`,
          kind: 'system.status',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: status.severity,
          category: status.category,
          label: status.label,
          explanation: status.explanation,
          nextStep: status.nextStep
        }
      ];
    }

    // Heuristic / capture-fail / parser-warning all arrive as orchestrator
    // lines. Inspect the text to pick the right kind.
    if (/\[capture-fail\]/i.test(firstLine.text)) {
      const cliMatch = /from\s+(\w+)/i.exec(firstLine.text);
      return [
        {
          id: `${baseId}:capture-fail`,
          kind: 'system.captureFail',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: 'warn',
          cliType: cliMatch?.[1] ?? 'unknown',
          fallback: 'rebuild from disk on next follow-up'
        }
      ];
    }
    if (/\[schema-drift\]/i.test(firstLine.text) || /report is unstructured/i.test(firstLine.text) || /failed to parse/i.test(firstLine.text)) {
      const dedupeKey = `schema-drift:${firstLine.text.trim()}`;
      if (seenParserDedupeKeys.has(dedupeKey)) return null;
      seenParserDedupeKeys.add(dedupeKey);
      const expectedRaw = /expected\s+([A-Za-z][\w-]*)/i.exec(firstLine.text)?.[1];
      const expected = expectedRaw
        ?? (/MetaCycle/i.test(firstLine.text) ? 'MetaCycleReport' : 'structured-report');
      return [
        {
          id: `${baseId}:schema-drift`,
          kind: 'system.schemaDrift',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: 'warn',
          expectedSchema: expected,
          message: firstLine.text.trim(),
          recovery: 'Open raw report and regenerate',
          rawLink: { range, label: 'Open raw report' },
          collapsedByDefault: true
        }
      ];
    }
    if (/could not classify/i.test(firstLine.text) || /\[heuristic\]/i.test(firstLine.text)) {
      const dedupeKey = `heuristic:${firstLine.text.trim()}`;
      if (seenParserDedupeKeys.has(dedupeKey)) return null;
      seenParserDedupeKeys.add(dedupeKey);
      return [
        {
          id: `${baseId}:parser-warning`,
          kind: 'system.parserWarning',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: 'warn',
          expectedKind: 'sentinel',
          message: firstLine.text.trim(),
          dedupeKey,
          collapsedByDefault: true
        }
      ];
    }
    if (/\[\[TASK_NEEDS_INPUT/i.test(firstLine.text) || /needs[- ]input/i.test(firstLine.text)) {
      const question = extractNeedsInputQuestion(firstLine.text);
      return [
        {
          id: `${baseId}:needs-input`,
          kind: 'agent.needsInput',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: 'warn',
          question: question ?? firstLine.text.trim(),
          loopIndex: 0,
          loopLimit: 0,
          answerSource: null,
          nextAction: 'await-human'
        }
      ];
    }

    // Fall back to a generic orchestrator decision row.
    const reason = firstLine.text.replace(/^\s*\[[^\]]+\]\s*/, '').trim();
    const decisionType = (/^\s*\[([^\]]+)\]/.exec(firstLine.text)?.[1] ?? 'decision').toLowerCase();
    return [
      {
        id: `${baseId}:decision`,
        kind: 'decision.orchestrator',
        timestamp: ts,
        runId,
        rawRange: range,
        decisionType,
        reason,
        action: decisionType === 'reissue' ? 'reissue' : undefined
      }
    ];
  }

  if (firstLine.stream === 'supervisor') {
    return [
      {
        id: `${baseId}:supervisor`,
        kind: 'message.supervisor',
        timestamp: ts,
        runId,
        rawRange: range,
        severity: group.status === 'error' ? 'error' : 'info',
        actor: 'Supervisor',
        body: group.title
      }
    ];
  }

  if (isCodexDebugGroup(group)) {
    return [
      {
        id: `${baseId}:codex-status`,
        kind: 'system.status',
        timestamp: ts,
        runId,
        rawRange: range,
        severity: 'info',
        category: 'codex',
        label: group.title.replace(/^Codex\s+/i, 'Codex '),
        explanation: codexLifecycleExplanation(group.title),
        nextStep: 'No action needed; raw frame is available in Trace.'
      }
    ];
  }

  // Tool / tool-error groups never reach this branch: the main loop
  // collapses contiguous tool activity into a single merged ToolBurst
  // before delegating non-tool groups here. That keeps every burst in
  // the conversation a single dense row and lets the renderer summarise
  // multi-family activity without re-walking the event list.
  if (group.kind === 'error' || group.status === 'error') {
    const routerError = parseToolRouterError(joinGroupBody(group));
    if (routerError) {
      return [
        {
          id: `${baseId}:tool-router`,
          kind: 'system.parserWarning',
          timestamp: ts,
          runId,
          rawRange: range,
          severity: 'warn',
          expectedKind: 'tool-result',
          message: routerError,
          dedupeKey: `tool-router:${routerError}`,
          collapsedByDefault: true
        }
      ];
    }
    return [
      {
        id: `${baseId}:agent-error`,
        kind: 'message.taskAgent',
        timestamp: ts,
        runId,
        rawRange: range,
        severity: 'error',
        actor: 'Agent',
        body: joinGroupBody(group)
      }
    ];
  }

  // Default: a regular task-agent message turn.
  return [
    {
      id: `${baseId}:agent`,
      kind: 'message.taskAgent',
      timestamp: ts,
      runId,
      rawRange: range,
      actor: 'Agent',
      body: joinGroupBody(group)
    }
  ];
}

// ──────────────────────────────────────────────────────────────────────────
// Tool burst summarisation
// ──────────────────────────────────────────────────────────────────────────

const TOOL_KINDS: readonly ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo'];

function isToolKind(k: ActivityLogKind): k is Exclude<ToolFamily, 'other'> {
  return TOOL_KINDS.includes(k);
}

/**
 * Returns the tool family for a group that should fold into a contiguous
 * tool burst, or `null` when the group is not tool-like. The error case
 * exists because the activity-log parser collapses any failing action into
 * kind='error', erasing the original verb; we recover it from the raw
 * line text so a failed `Run npm test` still folds into the burst with
 * `failures + 1` instead of escaping as a generic agent error message.
 */
function classifyToolGroup(group: ActivityLogGroup): ToolFamily | null {
  if (isToolKind(group.kind)) return group.kind as ToolFamily;
  if (group.kind === 'error') {
    const firstLine = group.lines[0];
    if (!firstLine) return null;
    return recoverToolFamilyFromErrorLine(firstLine.text);
  }
  return null;
}

function recoverToolFamilyFromErrorLine(text: string): ToolFamily | null {
  // Action lines are emitted as `<marker> <verb> ...`. The parser already
  // stripped the marker, but the original CliOutputLine still carries it.
  const m = /^[xX*]\s+(?<verb>Read|Search|Grep|Edit|Write|Run|Execute|Build|Check|Update|Apply|Move|Delete|Create|Task|Todo)\b/i.exec(text);
  if (!m) return null;
  const verb = m.groups!['verb'].toLowerCase();
  if (verb === 'read') return 'read';
  if (verb === 'search' || verb === 'grep') return 'search';
  if (verb === 'edit' || verb === 'write' || verb === 'create' || verb === 'delete' || verb === 'move' || verb === 'update' || verb === 'apply') return 'edit';
  if (verb === 'run' || verb === 'execute' || verb === 'build' || verb === 'check') return 'command';
  if (verb === 'task') return 'task';
  if (verb === 'todo') return 'todo';
  return null;
}

interface BurstMember {
  group: ActivityLogGroup;
  family: ToolFamily;
}

function toMergedToolBurst(
  members: readonly BurstMember[],
  range: RawLineRange,
  runId: number | undefined
) {
  const families: Partial<Record<ToolFamily, number>> = {};
  const samples: Record<string, string | undefined> = {};
  const files: string[] = [];
  const artifacts: string[] = [];
  const tests: { command: string; status: 'pass' | 'fail' | 'unknown' }[] = [];
  const commands: ToolCommandExecution[] = [];
  const allLines: CliOutputLine[] = [];

  let count = 0;
  let failures = 0;

  for (const { group, family } of members) {
    const batchSize = inferBatchSize(group);
    const isFailure = group.kind === 'error' || group.status === 'error';
    families[family] = (families[family] ?? 0) + batchSize;
    count += batchSize;
    if (isFailure) failures += batchSize;
    if (!samples[family]) {
      samples[family] = group.subtitle || stripBatchSuffix(group.title);
    }
    for (const path of collectFilePaths(group, family)) {
      if (!files.includes(path)) files.push(path);
      if (looksLikeArtifact(path) && !artifacts.includes(path)) {
        artifacts.push(path);
      }
    }
    if (family === 'command') {
      const test = detectTest(group);
      if (test) tests.push(test);
      const command = commandExecutionFromGroup(group);
      if (command) commands.push(command);
    }
    allLines.push(...group.lines);
  }

  // Tests sometimes appear as "run", "rerun", "passed in 320ms". Roll
  // multiple identical commands into a single test entry whose final
  // status is the latest non-unknown status seen, so a fail/retry/pass
  // pattern surfaces as one passing test rather than three rows.
  const collapsedTests = collapseTestsByCommand(tests);

  return {
    id: `${range.source}:${range.start}-${range.end}:tool`,
    kind: 'toolBurst' as const,
    timestamp: members[0].group.lines[0].timestamp,
    runId,
    rawRange: range,
    severity: failures > 0 ? ('error' as const) : ('info' as const),
    count,
    families,
    failures,
    durationMs: computeDurationMs(allLines),
    files: files.length > 0 ? files : undefined,
    artifacts: artifacts.length > 0 ? artifacts : undefined,
    tests: collapsedTests.length > 0 ? collapsedTests : undefined,
    commands: commands.length > 0 ? commands : undefined,
    samples,
    collapsedByDefault: true
  };
}

function isCodexDebugGroup(group: ActivityLogGroup): boolean {
  return group.kind === 'other' && /^Codex\b/i.test(group.title);
}

function codexLifecycleExplanation(title: string): string {
  const label = title.replace(/^Codex\s+/i, '').trim();
  if (/turn\.started/i.test(label)) return 'Codex started a model turn.';
  if (/turn\.completed/i.test(label)) return 'Codex completed the model turn.';
  if (/thread|session/i.test(label)) return 'Codex emitted session lifecycle metadata.';
  return 'Codex emitted a structured runtime frame.';
}

function parseToolRouterError(text: string): string | null {
  if (!/codex_core::tools::router/i.test(text)) return null;
  const exit = /Exit code:\s*(-?\d+)/i.exec(text)?.[1];
  return exit
    ? `Tool router reported exit code ${exit}.`
    : 'Tool router reported an execution error.';
}

interface ParsedStatus {
  category: string;
  label: string;
  explanation: string;
  nextStep?: string;
  severity: 'info' | 'warn' | 'error';
}

function parseOrchestratorStatus(text: string): ParsedStatus | null {
  const match = /^\s*\[([a-z0-9_.:-]+)\]\s*(.*)$/i.exec(text);
  if (!match) return null;
  const category = match[1].toLowerCase();
  const body = match[2].trim();
  switch (category) {
    case 'codex-silent-completion':
      return {
        category,
        label: 'Silent completion recovery',
        explanation: body || 'Codex stopped producing output after a final tool call, so the runner finalized the run through its recovery path.',
        nextStep: 'Review the result evidence; this is a recovery signal, not proof of completion.',
        severity: 'warn'
      };
    case 'watchdog':
    case 'watchdog-warning':
    case 'watchdog-timeout':
      return {
        category,
        label: 'Watchdog',
        explanation: body || 'The watchdog observed a quiet or stuck run.',
        nextStep: /kill|timeout/i.test(category + body) ? 'The runner will stop or escalate the run.' : 'Waiting for output or the timeout threshold.',
        severity: /timeout|kill|cancel/i.test(category + body) ? 'error' : 'warn'
      };
    case 'quarantined':
    case 'circuit-breaker':
      return {
        category,
        label: 'Circuit breaker',
        explanation: body || 'The runner stopped a repeated no-progress loop.',
        nextStep: 'Human review should inspect the repeated failure before rerunning.',
        severity: 'error'
      };
    case 'environment-blocker':
      return {
        category,
        label: 'Environment blocker',
        explanation: body || 'The local environment blocked the run.',
        nextStep: 'Fix the environment issue, then retry the task.',
        severity: 'error'
      };
    case 'worktree-containment':
      return {
        category,
        label: 'Worktree containment',
        explanation: body || 'The runner detected a worktree or path containment guard.',
        nextStep: 'Keep review inside the task worktree boundary.',
        severity: 'warn'
      };
    default:
      return null;
  }
}

const COMMAND_SUMMARY_RE = /^\$\s+(?<command>.*?)\s*(?:\[(?<status>[^\]]+)\])?\s*(?:\[exit\s+(?<exit>-?\d+)\])?\s*$/i;
function commandExecutionFromGroup(group: ActivityLogGroup): ToolCommandExecution | null {
  const first = group.lines[0]?.text ?? '';
  const parsed = COMMAND_SUMMARY_RE.exec(first);
  const command = (parsed?.groups?.['command'] ?? group.title).trim();
  if (!command) return null;
  const statusRaw = (parsed?.groups?.['status'] ?? '').toLowerCase();
  const exitRaw = parsed?.groups?.['exit'];
  const exitCode = exitRaw === undefined ? null : Number(exitRaw);
  const outputLines = group.lines.slice(parsed ? 1 : 0).map((l) => l.text ?? '');
  return {
    command,
    status: normalizeCommandStatus(statusRaw, group.status, Number.isFinite(exitCode) ? exitCode : null),
    exitCode: Number.isFinite(exitCode) ? exitCode : null,
    output: outputLines.join('\n').trimEnd(),
    outputLineCount: outputLines.length,
    outputTruncated: false,
    hits: parseOutputHits(outputLines)
  };
}

function normalizeCommandStatus(
  status: string,
  groupStatus: ActivityLogGroup['status'],
  exitCode: number | null
): ToolCommandExecution['status'] {
  if (/progress|running|started/.test(status)) return 'running';
  if (/fail|error|cancel/.test(status) || groupStatus === 'error' || (exitCode !== null && exitCode !== 0)) return 'failed';
  if (/complete|success|done/.test(status) || exitCode === 0) return 'completed';
  return 'unknown';
}

const HIT_RE = /^(?<path>(?:[A-Za-z]:)?[^:\n]+?):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<text>.*)$/;

function parseOutputHits(lines: readonly string[]): ToolOutputHit[] | undefined {
  const hits: ToolOutputHit[] = [];
  for (const raw of lines) {
    const match = HIT_RE.exec(raw.trim());
    if (!match?.groups) continue;
    const path = match.groups['path'].trim();
    if (!/[\\/]|[.][A-Za-z0-9]+$/.test(path)) continue;
    hits.push({
      path,
      line: Number(match.groups['line']),
      column: match.groups['col'] ? Number(match.groups['col']) : undefined,
      text: match.groups['text'] ?? ''
    });
    if (hits.length >= 40) break;
  }
  return hits.length > 0 ? hits : undefined;
}

function stripBatchSuffix(title: string): string {
  return title.replace(/\s*(?:×\d+|\(\d+\))\s*$/, '').trim();
}

/**
 * Pulls likely file paths out of a tool group. Read / search lines carry
 * the path in the title verb ("Read prompt.md") and again as the `|`
 * continuation. Edit groups put the path in the subtitle. Commands
 * generally do not name a file, so we leave their files alone.
 */
function collectFilePaths(group: ActivityLogGroup, family: ToolFamily): string[] {
  const out: string[] = [];
  const push = (raw: string | undefined): void => {
    if (!raw) return;
    const cleaned = raw.replace(/^[\s|`\\/_-]+/, '').trim();
    if (!cleaned) return;
    if (cleaned.length > 200) return;
    if (!/[./\\]/.test(cleaned)) return;
    if (!out.includes(cleaned)) out.push(cleaned);
  };
  if (family === 'read' || family === 'search' || family === 'edit') {
    push(group.subtitle);
    const verbMatch = /^(?:Read|Edit|Write|Create|Update|Apply|Delete|Move)\s+(.+)$/i.exec(stripBatchSuffix(group.title));
    if (verbMatch) push(verbMatch[1]);
  }
  for (const line of group.lines) {
    if (line.text && /^\s*\|/.test(line.text)) {
      push(line.text);
    }
  }
  return out;
}

function looksLikeArtifact(path: string): boolean {
  return /\.(png|jpg|jpeg|gif|svg|webp|pdf|html|json|md)$/i.test(path)
    || /(?:^|[\\/])(results|screenshots|artifacts|evidence)[\\/]/i.test(path);
}

const TEST_VERB_RE = /\b(test|spec|playwright|pytest|jest|vitest|mocha|xunit|dotnet test|npm (?:run )?test|npx playwright)\b/i;

function detectTest(group: ActivityLogGroup): { command: string; status: 'pass' | 'fail' | 'unknown' } | null {
  // The activity-log parser may have merged a fail / retry / pass run into
  // one "Commands ×N" batch group whose title no longer mentions "test".
  // Recover the test signal from any underlying line so the burst still
  // surfaces a Test rollup row in expanded mode.
  const actionLines = group.lines.filter((l) => /^\s*[xX*]\s+/.test(l.text));
  const looksLikeTest = TEST_VERB_RE.test(group.title)
    || actionLines.some((l) => TEST_VERB_RE.test(l.text));
  if (!looksLikeTest) return null;

  const sourceTitle = actionLines[0]?.text ?? group.title;
  const labelMatch = /^\s*[xX*]\s+(.+)$/.exec(sourceTitle);
  const baseTitle = stripBatchSuffix((labelMatch?.[1] ?? sourceTitle).trim());

  // Strip the "(shell)" trailer and the parser's "exited with error N" /
  // "failed" suffix so the de-dup key stays stable across pass/fail/retry
  // runs of the same command.
  const command = baseTitle
    .replace(/:\s*(?:exited with error\s*\d*|failed.*)$/i, '')
    .replace(/\s*\(shell\)\s*$/i, '')
    .trim();

  let status: 'pass' | 'fail' | 'unknown' = 'unknown';
  const isFailure = group.kind === 'error' || group.status === 'error'
    || /:\s*exited with error|\bfailed\b|\bFAIL\b/.test(group.title);
  const passEvidence = group.lines.some(
    (l) => /\bpassed\b|✓|\bsucceeded\b|\bOK\b|all tests pass/i.test(l.text)
  );
  if (passEvidence) status = 'pass';
  else if (isFailure) status = 'fail';
  return { command, status };
}

function collapseTestsByCommand(
  tests: readonly { command: string; status: 'pass' | 'fail' | 'unknown' }[]
): { command: string; status: 'pass' | 'fail' | 'unknown' }[] {
  const order: string[] = [];
  const map = new Map<string, 'pass' | 'fail' | 'unknown'>();
  for (const t of tests) {
    if (!map.has(t.command)) {
      order.push(t.command);
      map.set(t.command, t.status);
      continue;
    }
    const prev = map.get(t.command)!;
    // Latest non-unknown status wins so retry-then-pass surfaces as pass,
    // and pass-then-fail surfaces as fail.
    if (t.status !== 'unknown') map.set(t.command, t.status);
    else if (prev === 'unknown') map.set(t.command, t.status);
  }
  return order.map((cmd) => ({ command: cmd, status: map.get(cmd)! }));
}

function inferBatchSize(group: ActivityLogGroup): number {
  const m = /\s*(?:×(\d+)|\((\d+)\))\s*$/.exec(group.title);
  if (m) return Math.max(1, Number(m[1] ?? m[2]));
  return 1;
}

function computeDurationMs(lines: readonly CliOutputLine[]): number {
  let first = Number.POSITIVE_INFINITY;
  let last = Number.NEGATIVE_INFINITY;
  for (const l of lines) {
    const t = Date.parse(l.timestamp);
    if (!Number.isFinite(t)) continue;
    if (t < first) first = t;
    if (t > last) last = t;
  }
  return Number.isFinite(first) && Number.isFinite(last) && last > first ? last - first : 0;
}

// ──────────────────────────────────────────────────────────────────────────
// Parsing helpers
// ──────────────────────────────────────────────────────────────────────────

interface WatchdogParse {
  state: 'quiet' | 'resumed' | 'killed';
  quietSeconds: number;
  reason?: string;
}

function parseWatchdogText(text: string): WatchdogParse | null {
  if (!/\[watchdog[^\]]*\]/i.test(text)) return null;
  if (/killed after|auto-cancelled after/i.test(text)) {
    const sec = /([0-9]+(?:\.[0-9]+)?)\s*(?:s|sec|seconds)/i.exec(text);
    return { state: 'killed', quietSeconds: sec ? Number(sec[1]) : 0, reason: text.trim() };
  }
  if (/resumed|streaming output again/i.test(text)) {
    return { state: 'resumed', quietSeconds: 0, reason: text.trim() };
  }
  if (/quiet|silent|no output for/i.test(text)) {
    const sec = /([0-9]+(?:\.[0-9]+)?)\s*(?:s|sec|seconds)/i.exec(text);
    return { state: 'quiet', quietSeconds: sec ? Number(sec[1]) : 0, reason: text.trim() };
  }
  return { state: 'quiet', quietSeconds: 0, reason: text.trim() };
}

function extractNeedsInputQuestion(text: string): string | null {
  const m = /\[\[TASK_NEEDS_INPUT:([^\]]+)\]\]/i.exec(text);
  if (m) return m[1].trim();
  const idx = text.toLowerCase().indexOf('needs-input');
  if (idx >= 0) return text.slice(idx + 'needs-input'.length).replace(/^[:\s-]+/, '').trim();
  return null;
}

function extractUserTarget(text: string): string | undefined {
  const m = /->\s*(?:task|job)\s+([\w/-]+)/i.exec(text);
  return m?.[1];
}

function joinGroupBody(group: ActivityLogGroup): string {
  return group.lines.map((l) => l.text).filter((t) => t !== undefined).join('\n').trim();
}

// ──────────────────────────────────────────────────────────────────────────
// Range / run helpers
// ──────────────────────────────────────────────────────────────────────────

function numberLines(lines: readonly CliOutputLine[]): Map<CliOutputLine, number> {
  const map = new Map<CliOutputLine, number>();
  lines.forEach((l, i) => map.set(l, i + 1));
  return map;
}

function rangeForGroup(
  group: ActivityLogGroup,
  indexByLine: Map<CliOutputLine, number>,
  source: string
): RawLineRange {
  const indices: number[] = [];
  for (const l of group.lines) {
    const idx = indexByLine.get(l);
    if (idx !== undefined) indices.push(idx);
  }
  if (indices.length === 0) return { source, start: 1, end: 1 };
  indices.sort((a, b) => a - b);
  return { source, start: indices[0], end: indices[indices.length - 1] };
}

interface RunContext {
  run: RunRecord | null;
}

function pickInitialRun(timeline: RunTimeline | null): RunContext {
  if (!timeline || timeline.runs.length === 0) return { run: null };
  return { run: timeline.runs[0] };
}

function buildRunIndex(
  lines: readonly CliOutputLine[],
  timeline: RunTimeline | null
): Map<number, RunContext> {
  const map = new Map<number, RunContext>();
  if (!timeline || timeline.runs.length === 0) return map;
  for (const run of timeline.runs) {
    if (run.lineStart && run.lineStart > 0) {
      map.set(run.lineStart, { run });
    }
  }
  return map;
}

// ──────────────────────────────────────────────────────────────────────────
// Companion-evidence projection
// ──────────────────────────────────────────────────────────────────────────

function transcriptRange(ctx: ConversationProjectionContext, lineNumbers: Map<CliOutputLine, number>): RawLineRange {
  void lineNumbers;
  const len = ctx.lines.length;
  return { source: ctx.source, start: 1, end: Math.max(1, len) };
}

function toImageEvent(
  shot: ScreenshotEvidence,
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  return {
    id: `${range.source}:image:${shot.sourcePath}`,
    kind: 'artifact.image' as const,
    timestamp: shot.timestamp ?? ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    caption: shot.caption,
    sourcePath: shot.sourcePath,
    durablePath: shot.durablePath ?? null,
    sourceTool: shot.sourceTool,
    taskLink: shot.taskLink
  };
}

function toTaskTokenMetric(
  summary: TaskTokenSummary,
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  return {
    id: `${range.source}:metric:task-tokens`,
    kind: 'metric.token' as const,
    timestamp: summary.lastUpdate ?? ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    scope: 'task',
    inputTokens: summary.inputTokens,
    outputTokens: summary.outputTokens
  };
}

function toGitPreview(
  commits: readonly CommitEvidence[],
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  const files = commits.flatMap((c) => c.files);
  return {
    id: `${range.source}:workbench:git`,
    kind: 'workbench.gitPreview' as const,
    timestamp: commits[0]?.authorDateUtc ?? ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    files: files.map((f) => ({ status: f.status, path: f.path, added: f.added, removed: f.removed }))
  };
}

function toVisualPreview(
  shots: readonly ScreenshotEvidence[],
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  return {
    id: `${range.source}:workbench:visual`,
    kind: 'workbench.visualPreview' as const,
    timestamp: shots[0]?.timestamp ?? ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    images: shots.map((s) => ({ caption: s.caption, path: s.durablePath ?? s.sourcePath }))
  };
}

function toRunMarker(matched: RunContext, range: RawLineRange) {
  const run = matched.run!;
  return {
    id: `${range.source}:run:${run.index}`,
    kind: 'runMarker' as const,
    timestamp: run.startedAt,
    runId: run.index,
    rawRange: range,
    marker: run.intent,
    cli: run.cli,
    sessionId: run.capturedSessionId,
    durationSeconds: run.durationSeconds,
    exitCode: run.exitCode,
    traceRange:
      run.lineStart && run.lineEnd
        ? { source: range.source, start: run.lineStart, end: run.lineEnd }
        : undefined
  };
}

function toTaskMarker(
  job: TaskInfo,
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  return {
    id: `${range.source}:task:${job.id}`,
    kind: 'taskMarker' as const,
    timestamp: job.lastActivity ?? job.createdAt ?? ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    jobId: job.id,
    rawRange: range,
    marker: job.state,
    lane: job.state,
    title: job.title,
    tokens: job.tokenSummary
      ? { inputTokens: job.tokenSummary.inputTokens, outputTokens: job.tokenSummary.outputTokens }
      : undefined
  };
}

function toWorkbenchSummary(
  collected: ConversationEvent[],
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  const aggregate = computeSummaryAggregate(collected, ctx);

  const headlineParts: string[] = [];
  if (aggregate.toolCallCount && aggregate.toolCallCount > 0) {
    headlineParts.push(`${aggregate.toolCallCount} tool call${aggregate.toolCallCount === 1 ? '' : 's'}`);
  }
  if (aggregate.toolFailureCount && aggregate.toolFailureCount > 0) {
    headlineParts.push(`${aggregate.toolFailureCount} failure${aggregate.toolFailureCount === 1 ? '' : 's'}`);
  }
  if (aggregate.commitCount && aggregate.commitCount > 0) {
    headlineParts.push(`${aggregate.commitCount} commit${aggregate.commitCount === 1 ? '' : 's'}`);
  }
  if (aggregate.filesChanged && aggregate.filesChanged > 0) {
    headlineParts.push(`${aggregate.filesChanged} file${aggregate.filesChanged === 1 ? '' : 's'}`);
  }
  if (aggregate.screenshotCount && aggregate.screenshotCount > 0) {
    headlineParts.push(`${aggregate.screenshotCount} screenshot${aggregate.screenshotCount === 1 ? '' : 's'}`);
  }
  if (aggregate.retryWarningCount && aggregate.retryWarningCount > 0) {
    headlineParts.push(`${aggregate.retryWarningCount} warning${aggregate.retryWarningCount === 1 ? '' : 's'}`);
  }
  if (aggregate.totalInputTokens || aggregate.totalOutputTokens) {
    const tokens = (aggregate.totalInputTokens ?? 0) + (aggregate.totalOutputTokens ?? 0);
    headlineParts.push(`${formatCount(tokens)} tokens`);
  }
  if (aggregate.latestResult) {
    headlineParts.push(aggregate.latestResult);
  }

  const bullets: string[] = [];
  if (aggregate.runCount && aggregate.runCount > 0) {
    const status = aggregate.latestRunStatus ? ` (${aggregate.latestRunStatus})` : '';
    bullets.push(`${aggregate.runCount} run${aggregate.runCount === 1 ? '' : 's'}${status}`);
  }
  if (aggregate.totalDurationSeconds && aggregate.totalDurationSeconds > 0) {
    bullets.push(`Duration: ${formatDurationSec(aggregate.totalDurationSeconds)}`);
  }
  if (aggregate.watchdogKilled) {
    bullets.push('Watchdog killed at least one run');
  }
  if (aggregate.state) {
    bullets.push(`Lane: ${aggregate.state}`);
  }

  return {
    id: `${range.source}:workbench:summary`,
    kind: 'workbench.summary' as const,
    timestamp: ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    headline: headlineParts.length > 0 ? headlineParts.join(' · ') : 'No activity yet',
    bullets: bullets.length > 0 ? bullets : undefined,
    aggregate
  };
}

function computeSummaryAggregate(
  collected: ConversationEvent[],
  ctx: ConversationProjectionContext
): WorkbenchSummaryAggregate {
  const toolBursts = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'toolBurst' }> => e.kind === 'toolBurst'
  );
  const tokenMetrics = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'metric.token' }> => e.kind === 'metric.token'
  );
  const taskScopedTokens = tokenMetrics.find((m) => m.scope === 'task') ?? tokenMetrics[0];
  const supervisorWaits = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'supervisor.wait' }> => e.kind === 'supervisor.wait'
  );
  const parserWarnings = collected.filter((e) => e.kind === 'system.parserWarning');
  const captureFails = collected.filter((e) => e.kind === 'system.captureFail');
  const schemaDrifts = collected.filter((e) => e.kind === 'system.schemaDrift');
  const decisionRetries = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'decision.orchestrator' }> =>
      e.kind === 'decision.orchestrator' && e.action === 'reissue'
  );

  const toolCallCount = toolBursts.reduce((acc, b) => acc + b.count, 0);
  const toolFailureCount = toolBursts.reduce((acc, b) => acc + b.failures, 0);
  const retryWarningCount =
    parserWarnings.length + captureFails.length + schemaDrifts.length + decisionRetries.length;

  const filesFromCommits = (ctx.commits ?? []).reduce(
    (acc, c) => acc + (c.files?.length ?? 0),
    0
  );

  const timeline = ctx.runTimeline ?? null;
  const latestRun = timeline && timeline.runs.length > 0
    ? timeline.runs[timeline.runs.length - 1]
    : null;

  const totalDurationSeconds = timeline
    ? timeline.runs.reduce((acc, r) => acc + (r.durationSeconds ?? 0), 0)
    : undefined;

  return {
    state: ctx.job?.state,
    runCount: timeline?.runCount,
    latestRunStatus: latestRun?.status,
    latestRunIntent: latestRun?.intent,
    totalDurationSeconds: totalDurationSeconds && totalDurationSeconds > 0 ? totalDurationSeconds : undefined,
    totalInputTokens: taskScopedTokens?.inputTokens,
    totalOutputTokens: taskScopedTokens?.outputTokens,
    toolCallCount,
    toolFailureCount,
    commitCount: ctx.commits?.length,
    filesChanged: filesFromCommits || undefined,
    screenshotCount: ctx.screenshots?.length,
    retryWarningCount: retryWarningCount > 0 ? retryWarningCount : undefined,
    watchdogKilled: supervisorWaits.some((w) => w.state === 'killed') || undefined,
    latestResult: ctx.latestResult
  };
}

function toWorkbenchDebug(
  collected: ConversationEvent[],
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  const messages = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: `message.${string}` }> =>
      e.kind.startsWith('message.')
  );
  const toolBursts = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'toolBurst' }> => e.kind === 'toolBurst'
  );
  const supervisorWaits = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'supervisor.wait' }> => e.kind === 'supervisor.wait'
  );
  const tokenMetrics = collected.filter(
    (e): e is Extract<ConversationEvent, { kind: 'metric.token' }> => e.kind === 'metric.token'
  );

  const families: Partial<Record<ToolFamily, number>> = {};
  let toolFailures = 0;
  for (const burst of toolBursts) {
    toolFailures += burst.failures;
    for (const [family, count] of Object.entries(burst.families)) {
      families[family as ToolFamily] = (families[family as ToolFamily] ?? 0) + (count ?? 0);
    }
  }

  const timeline = ctx.runTimeline ?? null;
  const completedCount = timeline ? timeline.runs.filter((r) => r.status === 'completed').length : 0;
  const failedCount = timeline ? timeline.runs.filter((r) => r.status === 'failed').length : 0;
  const cancelledCount = timeline ? timeline.runs.filter((r) => r.status === 'cancelled').length : 0;

  const traceLinks: TraceLink[] = collected
    .filter((e) => e.kind === 'runMarker' || e.kind === 'toolBurst' || e.kind === 'system.parserWarning')
    .slice(0, 12)
    .map((e) => ({
      range: e.rawRange,
      label: `${e.kind} @ ${e.rawRange.start}-${e.rawRange.end}`
    }));

  return {
    id: `${range.source}:workbench:debug`,
    kind: 'workbench.debug' as const,
    timestamp: ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    actorCounts: {
      user: messages.filter((m) => m.kind === 'message.user').length,
      taskAgent: messages.filter((m) => m.kind === 'message.taskAgent').length,
      orchestrator: messages.filter((m) => m.kind === 'message.orchestrator').length,
      supervisor: messages.filter((m) => m.kind === 'message.supervisor').length,
      supportingAgent: messages.filter((m) => m.kind === 'message.supportingAgent').length
    },
    toolDensity: {
      total: toolBursts.reduce((acc, b) => acc + b.count, 0),
      failures: toolFailures,
      families
    },
    warningCounts: {
      supervisorAdvisories: messages.filter((m) => m.kind === 'message.supervisor').length,
      parserWarnings: collected.filter((e) => e.kind === 'system.parserWarning').length,
      captureFails: collected.filter((e) => e.kind === 'system.captureFail').length,
      schemaDrifts: collected.filter((e) => e.kind === 'system.schemaDrift').length,
      needsInputLoops: collected.filter((e) => e.kind === 'agent.needsInput').length,
      watchdogQuiet: supervisorWaits.filter((w) => w.state === 'quiet').length,
      watchdogKills: supervisorWaits.filter((w) => w.state === 'killed').length
    },
    tokenTotals: {
      inputTokens: tokenMetrics.reduce((acc, m) => acc + (m.inputTokens ?? 0), 0),
      outputTokens: tokenMetrics.reduce((acc, m) => acc + (m.outputTokens ?? 0), 0),
      reasoningTokens: tokenMetrics.reduce((acc, m) => acc + (m.reasoningTokens ?? 0), 0),
      cost: tokenMetrics.reduce((acc, m) => acc + (m.cost ?? 0), 0) || undefined
    },
    runStats: {
      runCount: timeline?.runCount ?? 0,
      completedCount,
      failedCount,
      cancelledCount
    },
    traceLinks
  };
}

function formatCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
  return `${n}`;
}

function formatDurationSec(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  if (m < 60) return s === 0 ? `${m}m` : `${m}m ${s}s`;
  const h = Math.floor(m / 60);
  const min = m % 60;
  return min === 0 ? `${h}h` : `${h}h ${min}m`;
}

function toTraceLink(
  ctx: ConversationProjectionContext,
  lineNumbers: Map<CliOutputLine, number>
) {
  const range = transcriptRange(ctx, lineNumbers);
  return {
    id: `${range.source}:trace`,
    kind: 'traceLink' as const,
    timestamp: ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    target: 'raw-log',
    label: 'Open raw activity log',
    link: { range, label: 'Raw activity log' }
  };
}
