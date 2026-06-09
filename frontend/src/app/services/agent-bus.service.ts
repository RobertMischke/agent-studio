import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, catchError, map, of } from 'rxjs';
import {
  AgentMessage,
  AgentMessageQuery,
  AgentMessageSummary,
  TokenAggregateResponse,
} from '../models/agent-bus.model';
import { MODEL_IDS } from '../features/cli';

/**
 * Read-only client for the project-screen Observability panel. Wraps the
 * `/api/bus/{project}/...` endpoints in `BusEndpoints.cs`. The panel polls
 * these read paths; every request is best-effort and falls back to an
 * empty payload on transport error so the UI stays calm in offline / dev
 * scenarios.
 *
 * Fixture-backed development path: when the backend has not yet projected
 * any bus messages for a project (`totalMessages === 0`), the panel can
 * surface a synthetic dataset via {@link AgentBusFixture.SAMPLE} so the
 * surface remains testable in screenshots without waiting for live
 * traffic. The toggle is per-component; this service never injects fake
 * data into a real-empty project on its own.
 */
@Injectable({ providedIn: 'root' })
export class AgentBusService {
  private readonly http = inject(HttpClient);

  getSummary(project: string): Observable<AgentMessageSummary | null> {
    const url = `/api/bus/${encodeURIComponent(project)}/summary`;
    return this.http.get<AgentMessageSummary>(url).pipe(
      map(s => s ?? null),
      catchError(() => of<AgentMessageSummary | null>(null)),
    );
  }

  getRecent(project: string, limit = 200): Observable<AgentMessage[]> {
    const url = `/api/bus/${encodeURIComponent(project)}/recent`;
    const params = new HttpParams().set('limit', String(limit));
    return this.http.get<AgentMessage[]>(url, { params }).pipe(
      map(items => items ?? []),
      catchError(() => of<AgentMessage[]>([])),
    );
  }

  queryMessages(project: string, query: AgentMessageQuery): Observable<AgentMessage[]> {
    const url = `/api/bus/${encodeURIComponent(project)}/messages`;
    let params = new HttpParams();
    const set = (k: string, v: string | number | null | undefined) => {
      if (v === null || v === undefined || v === '') return;
      params = params.set(k, String(v));
    };
    set('jobId', query.jobId);
    set('runId', query.runId);
    set('participantId', query.participantId);
    set('kind', query.kind);
    set('severity', query.severity);
    set('cli', query.cli);
    set('skill', query.skill);
    set('tag', query.tag);
    set('correlationId', query.correlationId);
    set('since', query.since);
    set('until', query.until);
    set('limit', query.limit ?? null);
    return this.http.get<AgentMessage[]>(url, { params }).pipe(
      map(items => items ?? []),
      catchError(() => of<AgentMessage[]>([])),
    );
  }

  getMessage(project: string, id: string): Observable<AgentMessage | null> {
    const url = `/api/bus/${encodeURIComponent(project)}/messages/${encodeURIComponent(id)}`;
    return this.http.get<AgentMessage>(url).pipe(
      map(m => m ?? null),
      catchError(() => of<AgentMessage | null>(null)),
    );
  }

  /**
   * Token-spend rollup for one project. Backed by `BusAggregationCache` so
   * the unfiltered request is O(1); since/until trigger a fast in-memory
   * pass over the projection.
   */
  getTokenAggregate(
    project: string,
    options: { since?: string; until?: string } = {},
  ): Observable<TokenAggregateResponse | null> {
    const url = `/api/bus/${encodeURIComponent(project)}/token-aggregate`;
    let params = new HttpParams();
    if (options.since) params = params.set('since', options.since);
    if (options.until) params = params.set('until', options.until);
    return this.http.get<TokenAggregateResponse>(url, { params }).pipe(
      map(r => r ?? null),
      catchError(() => of<TokenAggregateResponse | null>(null)),
    );
  }
}

/**
 * Synthetic dataset used when the live bus is empty for a project. Sized
 * to exercise every surface in the panel: timeline gaps (silent periods),
 * per-participant rows, multiple kinds and severities, token-usage cells
 * for the heatmap, an error, and an intervention.
 *
 * The dataset is intentionally bounded (~30 messages) so the panel stays
 * snappy in fixture mode and Playwright runs are cheap.
 */
export const AgentBusFixture = {
  /** Returns a deterministic synthetic dataset anchored to `now`. */
  sample(project: string, now: Date = new Date()): AgentMessage[] {
    const t = (offsetMs: number) => new Date(now.getTime() + offsetMs).toISOString();
    const baseTags = ['fixture'];
    const out: AgentMessage[] = [
      {
        schemaVersion: 1,
        id: 'fx-001',
        createdAt: t(-1000 * 60 * 60 * 6),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'lifecycle',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        topic: 'pickup',
        summary: 'Picked up sample-onboard-runner from 2-ready.',
        tags: [...baseTags, 'pickup'],
      },
      {
        schemaVersion: 1,
        id: 'fx-002',
        createdAt: t(-1000 * 60 * 60 * 5 - 1000 * 50),
        participantId: 'claude',
        role: 'actor',
        kind: 'observation',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-1',
        cliSessionId: 'claude-sess-aaaa',
        summary: 'Read AGENTS.md and the runner architecture section.',
        body: 'Skimmed AGENTS.md sections 1-4 plus README.md to ground the task.',
        tokens: { input: 4200, output: 220, model: MODEL_IDS.claudeHaiku45 },
        tags: [...baseTags, 'cli:claude', 'skill:cli-claude'],
      },
      {
        schemaVersion: 1,
        id: 'fx-003',
        createdAt: t(-1000 * 60 * 60 * 5),
        participantId: 'claude',
        role: 'actor',
        kind: 'token-usage',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-1',
        cliSessionId: 'claude-sess-aaaa',
        summary: 'Token report after first pass.',
        tokens: { input: 12500, output: 2900, cacheRead: 8400, model: MODEL_IDS.claudeHaiku45, dollars: 0.0124 },
        tags: [...baseTags, 'cli:claude'],
      },
      {
        schemaVersion: 1,
        id: 'fx-004',
        createdAt: t(-1000 * 60 * 60 * 4 - 1000 * 60 * 12),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'decision',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        topic: 'reissue',
        summary: 'Re-issued the task with stronger framing after a fast NoOp.',
        body: 'Outcome policy detected MatchedSentinel=false on a UserContinue with follow-up; re-issued once.',
        replyToId: 'fx-002',
        tags: [...baseTags, 'orchestrator-chat', 'reissue'],
      },
      {
        schemaVersion: 1,
        id: 'fx-005',
        createdAt: t(-1000 * 60 * 60 * 4),
        participantId: 'supervisor',
        role: 'system',
        kind: 'advisory',
        severity: 'Warn',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-2',
        topic: 'silence',
        summary: 'No CLI output for 90s on run-2; advisory recorded.',
        tags: [...baseTags, 'supervisor', 'silence-90s'],
      },
      {
        schemaVersion: 1,
        id: 'fx-006',
        createdAt: t(-1000 * 60 * 60 * 3 - 1000 * 60 * 30),
        participantId: 'claude',
        role: 'actor',
        kind: 'question',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-2',
        summary: 'Asked the user to confirm the orchestration boundary.',
        body: 'Should the new pickup retry path live in ProjectRunner or a new helper service?',
        tags: [...baseTags, 'cli:claude', 'needs-input'],
      },
      {
        schemaVersion: 1,
        id: 'fx-007',
        createdAt: t(-1000 * 60 * 60 * 3),
        participantId: 'user',
        role: 'actor',
        kind: 'decision',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-3',
        replyToId: 'fx-006',
        summary: 'User: keep it inside ProjectRunner; new helper later if it spreads.',
        tags: [...baseTags, 'user-reply'],
      },
      {
        schemaVersion: 1,
        id: 'fx-008',
        createdAt: t(-1000 * 60 * 60 * 2 - 1000 * 60 * 50),
        participantId: 'codex',
        role: 'actor',
        kind: 'observation',
        severity: 'Info',
        project,
        jobId: 'sample-meta-cycle-tweak',
        runId: 'run-1',
        cliSessionId: 'codex-sess-bbbb',
        summary: 'Codex inspected the meta-cycle report header.',
        tokens: { input: 1800, output: 150, model: MODEL_IDS.gpt5Codex },
        tags: [...baseTags, 'cli:codex'],
      },
      {
        schemaVersion: 1,
        id: 'fx-009',
        createdAt: t(-1000 * 60 * 60 * 2 - 1000 * 60 * 30),
        participantId: 'codex',
        role: 'actor',
        kind: 'token-usage',
        severity: 'Info',
        project,
        jobId: 'sample-meta-cycle-tweak',
        runId: 'run-1',
        cliSessionId: 'codex-sess-bbbb',
        summary: 'Token report after planning pass.',
        tokens: { input: 7900, output: 950, model: MODEL_IDS.gpt5Codex, dollars: 0.0089 },
        tags: [...baseTags, 'cli:codex'],
      },
      {
        schemaVersion: 1,
        id: 'fx-010',
        createdAt: t(-1000 * 60 * 60 * 2),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'intervention',
        severity: 'High',
        project,
        jobId: 'sample-meta-cycle-tweak',
        topic: 'cancel-run',
        summary: 'Cancelled run-1 after pickup-failure threshold tripped.',
        tags: [...baseTags, 'intervention', 'pickup-failure'],
      },
      {
        schemaVersion: 1,
        id: 'fx-011',
        createdAt: t(-1000 * 60 * 60 * 1 - 1000 * 60 * 40),
        participantId: 'security-audit',
        role: 'evidence',
        kind: 'artifact',
        severity: 'Info',
        project,
        jobId: 'sample-security-audit',
        topic: 'audit-result',
        summary: 'Markdown audit report generated.',
        artifacts: [
          { kind: 'markdown-report', uri: 'logs/security/audit-2026-05-06.md', label: 'Security audit 2026-05-06' },
        ],
        tags: [...baseTags, 'skill:security-review'],
      },
      {
        schemaVersion: 1,
        id: 'fx-012',
        createdAt: t(-1000 * 60 * 60 * 1 - 1000 * 60 * 20),
        participantId: 'claude',
        role: 'actor',
        kind: 'error',
        severity: 'High',
        project,
        jobId: 'sample-security-audit',
        runId: 'run-1',
        cliSessionId: 'claude-sess-cccc',
        summary: 'CLI exited with non-zero code while writing the report.',
        body: 'stderr: failed to open logs/security/: ENOENT.',
        tags: [...baseTags, 'cli:claude', 'error'],
      },
      {
        schemaVersion: 1,
        id: 'fx-013',
        createdAt: t(-1000 * 60 * 60 * 1),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'decision',
        severity: 'Info',
        project,
        jobId: 'sample-security-audit',
        topic: 'reissue',
        summary: 'Re-issued the audit task with explicit results/ path.',
        replyToId: 'fx-012',
        tags: [...baseTags, 'orchestrator-chat'],
      },
      {
        schemaVersion: 1,
        id: 'fx-014',
        createdAt: t(-1000 * 60 * 50),
        participantId: 'claude',
        role: 'actor',
        kind: 'token-usage',
        severity: 'Info',
        project,
        jobId: 'sample-security-audit',
        runId: 'run-2',
        cliSessionId: 'claude-sess-cccc',
        summary: 'Re-run token report.',
        tokens: { input: 10100, output: 3300, cacheRead: 6200, model: MODEL_IDS.claudeSonnet46, dollars: 0.0341 },
        tags: [...baseTags, 'cli:claude'],
      },
      {
        schemaVersion: 1,
        id: 'fx-015',
        createdAt: t(-1000 * 60 * 35),
        participantId: 'claude',
        role: 'actor',
        kind: 'observation',
        severity: 'Info',
        project,
        jobId: 'sample-security-audit',
        runId: 'run-2',
        summary: 'Wrote the audit report and the screenshot.',
        artifacts: [
          { kind: 'screenshot', uri: 'results/audit-final.png' },
        ],
        tags: [...baseTags, 'cli:claude'],
      },
      {
        schemaVersion: 1,
        id: 'fx-016',
        createdAt: t(-1000 * 60 * 30),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'lifecycle',
        severity: 'Info',
        project,
        jobId: 'sample-security-audit',
        topic: 'auto-review',
        summary: 'Promoted job to 4-auto-review.',
        tags: [...baseTags, 'lifecycle'],
      },
      {
        schemaVersion: 1,
        id: 'fx-017',
        createdAt: t(-1000 * 60 * 24),
        participantId: 'system-review',
        role: 'system',
        kind: 'observation',
        severity: 'Info',
        project,
        topic: 'review',
        summary: 'Layer-3 system review captured a baseline snapshot.',
        artifacts: [
          { kind: 'markdown-report', uri: 'logs/system-review/2026-05-06-1100.md' },
        ],
        tags: [...baseTags, 'system-review'],
      },
      {
        schemaVersion: 1,
        id: 'fx-018',
        createdAt: t(-1000 * 60 * 18),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'heartbeat',
        severity: 'Info',
        project,
        summary: 'Pickup tick (idle).',
        tags: [...baseTags, 'heartbeat'],
      },
      {
        schemaVersion: 1,
        id: 'fx-019',
        createdAt: t(-1000 * 60 * 12),
        participantId: 'orchestrator',
        role: 'system',
        kind: 'heartbeat',
        severity: 'Info',
        project,
        summary: 'Pickup tick (idle).',
        tags: [...baseTags, 'heartbeat'],
      },
      {
        schemaVersion: 1,
        id: 'fx-020',
        createdAt: t(-1000 * 60 * 6),
        participantId: 'claude',
        role: 'actor',
        kind: 'token-usage',
        severity: 'Info',
        project,
        jobId: 'sample-onboard-runner',
        runId: 'run-3',
        cliSessionId: 'claude-sess-aaaa',
        summary: 'Closing token report after acceptance.',
        tokens: { input: 5400, output: 800, cacheRead: 3100, model: MODEL_IDS.claudeHaiku45, dollars: 0.0061 },
        tags: [...baseTags, 'cli:claude'],
      },
    ];
    return out;
  },

  /** Synthetic summary that matches {@link sample}. */
  summary(project: string, messages: AgentMessage[]): AgentMessageSummary {
    const countsByKind: Record<string, number> = {};
    const countsByParticipant: Record<string, number> = {};
    const countsBySeverity: Record<string, number> = {};
    let firstAt: string | null = null;
    let lastAt: string | null = null;
    for (const m of messages) {
      countsByKind[m.kind] = (countsByKind[m.kind] ?? 0) + 1;
      countsByParticipant[m.participantId] = (countsByParticipant[m.participantId] ?? 0) + 1;
      if (m.severity) {
        countsBySeverity[m.severity] = (countsBySeverity[m.severity] ?? 0) + 1;
      }
      if (!firstAt || m.createdAt < firstAt) firstAt = m.createdAt;
      if (!lastAt || m.createdAt > lastAt) lastAt = m.createdAt;
    }
    return {
      project,
      totalMessages: messages.length,
      firstMessageAt: firstAt,
      lastMessageAt: lastAt,
      countsByKind,
      countsByParticipant,
      countsBySeverity,
    };
  },
};
