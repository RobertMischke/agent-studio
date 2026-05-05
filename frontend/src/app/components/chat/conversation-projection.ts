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

import type {
  CliOutputLine,
  GitFileChange,
  JobInfo,
  JobTokenSummary,
  RunRecord,
  RunTimeline
} from '../../models/job.model';
import {
  parseActivityLog,
  type ActivityLogGroup,
  type ActivityLogKind
} from '../activity-log.parser';
import type {
  ConversationEvent,
  RawLineRange,
  ToolBurstSamples,
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
  files: ReadonlyArray<GitFileChange>;
  runIndex?: number;
}

export interface ConversationProjectionContext {
  /** Source identifier kept on every `RawLineRange` (job id is preferred). */
  source: string;
  /** Raw activity log lines. The projection numbers them 1-based for ranges. */
  lines: ReadonlyArray<CliOutputLine>;
  job?: JobInfo | null;
  runTimeline?: RunTimeline | null;
  tokenSummary?: JobTokenSummary | null;
  screenshots?: ReadonlyArray<ScreenshotEvidence>;
  commits?: ReadonlyArray<CommitEvidence>;
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
    if (matchedRun && matchedRun.run.index !== currentRun?.run?.index) {
      currentRun = matchedRun;
      if (ctx.emitRunMarkers) {
        events.push(toRunMarker(matchedRun, range));
      }
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
    if (/\[watchdog\]/i.test(firstLine.text)) {
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
      const expected = /expected\s+([A-Za-z][\w.-]*)/i.exec(firstLine.text)?.[1]
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

  if (isToolKind(group.kind)) {
    return [toToolBurst(group, range, runId)];
  }

  if (group.kind === 'error' || group.status === 'error') {
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

const TOOL_KINDS: ReadonlyArray<ActivityLogKind> = ['read', 'search', 'command', 'edit', 'task', 'todo'];

function isToolKind(k: ActivityLogKind): k is Exclude<ToolFamily, 'other'> {
  return TOOL_KINDS.includes(k);
}

function toToolBurst(group: ActivityLogGroup, range: RawLineRange, runId?: number) {
  const families: Partial<Record<ToolFamily, number>> = {};
  const samples: ToolBurstSamples = {};
  const family = group.kind as ToolFamily;
  const count = inferBatchSize(group);
  families[family] = count;
  samples[family] = group.subtitle || group.title;

  const failures = group.status === 'error' ? count : 0;
  const durationMs = computeDurationMs(group.lines);
  const files: string[] = [];
  if (family === 'edit' && group.subtitle) files.push(group.subtitle);

  return {
    id: `${range.source}:${range.start}-${range.end}:tool`,
    kind: 'toolBurst' as const,
    timestamp: group.lines[0].timestamp,
    runId,
    rawRange: range,
    severity: failures > 0 ? ('error' as const) : ('info' as const),
    count,
    families,
    failures,
    durationMs,
    files: files.length > 0 ? files : undefined,
    samples,
    collapsedByDefault: true
  };
}

function inferBatchSize(group: ActivityLogGroup): number {
  const m = /\s*(?:×(\d+)|\((\d+)\))\s*$/.exec(group.title);
  if (m) return Math.max(1, Number(m[1] ?? m[2]));
  return 1;
}

function computeDurationMs(lines: ReadonlyArray<CliOutputLine>): number {
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
  if (!/\[watchdog\]/i.test(text)) return null;
  if (/killed after/i.test(text)) {
    const sec = /([0-9]+(?:\.[0-9]+)?)\s*(?:s|sec|seconds)/i.exec(text);
    return { state: 'killed', quietSeconds: sec ? Number(sec[1]) : 0, reason: text.trim() };
  }
  if (/resumed/i.test(text)) {
    return { state: 'resumed', quietSeconds: 0, reason: text.trim() };
  }
  if (/quiet|silent/i.test(text)) {
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

function numberLines(lines: ReadonlyArray<CliOutputLine>): Map<CliOutputLine, number> {
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
  lines: ReadonlyArray<CliOutputLine>,
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

function transcriptRange(ctx: ConversationProjectionContext, _: Map<CliOutputLine, number>): RawLineRange {
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
  summary: JobTokenSummary,
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
  commits: ReadonlyArray<CommitEvidence>,
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
  shots: ReadonlyArray<ScreenshotEvidence>,
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
  job: JobInfo,
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
  const toolBursts = collected.filter((e): e is Extract<ConversationEvent, { kind: 'toolBurst' }> => e.kind === 'toolBurst');
  const totalCalls = toolBursts.reduce((acc, b) => acc + b.count, 0);
  const failures = toolBursts.reduce((acc, b) => acc + b.failures, 0);
  const headlineParts = [`${totalCalls} tool call${totalCalls === 1 ? '' : 's'}`];
  if (failures > 0) headlineParts.push(`${failures} failure${failures === 1 ? '' : 's'}`);
  return {
    id: `${range.source}:workbench:summary`,
    kind: 'workbench.summary' as const,
    timestamp: ctx.lines[0]?.timestamp ?? new Date(0).toISOString(),
    rawRange: range,
    headline: headlineParts.join(' · ')
  };
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
