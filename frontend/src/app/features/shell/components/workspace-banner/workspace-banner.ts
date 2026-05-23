import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NotificationComponent, NotificationKind } from '../../../../components/notification/notification.component';

/**
 * Slim workspace top-banner that surfaces the latest orchestrator-review
 * decision across the active projects: "Orchestrator decided X for Y".
 *
 * Reads `/api/bus/{project}/messages?kind=decision&tag=orchestrator-chat`
 * for each active project, picks the newest hit, and renders it for at
 * least 30 seconds. A fresh decision arriving within that window replaces
 * the current one. Outside the window the banner stays hidden.
 *
 * The banner is observability only: clicking it does not move state. The
 * canonical decision record lives in the per-project journal at
 * `{workspace}/logs/decisions/{project}.jsonl`; the banner just makes it
 * visible the moment the orchestrator acts.
 */
interface BusDecisionMessage {
  id: string;
  createdAt: string;
  kind: string;
  topic?: string | null;
  summary?: string | null;
  jobId?: string | null;
  project?: string | null;
  tags?: string[] | null;
}

interface DisplayDecision {
  id: string;
  createdAt: number;
  topic: string;
  jobId: string;
  project: string;
  summary: string;
}

const POLL_INTERVAL_MS = 5000;
const MIN_DISPLAY_MS = 30000;

/**
 * Topics that are legitimate auto-review verdicts and belong on the
 * banner. Bus messages with `kind=decision&tag=orchestrator-chat` also
 * carry watchdog state notes (silence warnings) that share the
 * orchestrator stream but are not a review verdict; gating by this list
 * keeps "Still silent at 61s" from rendering as "Orchestrator decided
 * accept". See `OrchestratorMessageKind.ToBusTopic()` for the source of
 * truth.
 */
const BANNER_TOPICS: ReadonlySet<string> = new Set([
  'decision',
  'reissue',
  'escalate',
  'giveup',
  'accept',
  'accept-as-done',
]);

@Component({
  selector: 'app-workspace-banner',
  standalone: true,
  imports: [NotificationComponent],
  templateUrl: './workspace-banner.html',
  styleUrl: './workspace-banner.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WorkspaceBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);

  /** Active project names to poll. The component fans out one request per project per tick. */
  readonly projects = input<readonly string[]>([]);

  private readonly current = signal<DisplayDecision | null>(null);
  private readonly dismissedId = signal<string | null>(null);
  private readonly nowTick = signal<number>(Date.now());

  /** What to render right now: latest non-dismissed decision still inside its 30s window. */
  readonly displayed = computed<DisplayDecision | null>(() => {
    const msg = this.current();
    if (!msg) return null;
    if (this.dismissedId() === msg.id) return null;
    if (this.nowTick() - msg.createdAt > MIN_DISPLAY_MS) return null;
    return msg;
  });

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private clockTimer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.pollNow();
    this.pollTimer = setInterval(() => this.pollNow(), POLL_INTERVAL_MS);
    // Re-evaluate the displayed window every second so the banner
    // disappears at the 30-second mark even without a new poll.
    this.clockTimer = setInterval(() => this.nowTick.set(Date.now()), 1000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    if (this.clockTimer) clearInterval(this.clockTimer);
  }

  dismiss(): void {
    const msg = this.current();
    if (msg) this.dismissedId.set(msg.id);
  }

  /** Topic-to-glyph mapping for the leading icon. */
  iconFor(topic: string): string {
    switch (topic) {
      case 'reissue':   return '↺';
      case 'escalate':  return '⚠';
      case 'decision':  return '✓';
      case 'giveup':    return '✕';
      case 'heuristic': return '~';
      default:          return '🤖';
    }
  }

  /**
   * Map an orchestrator-verdict topic to the unified notification's
   * severity variant. `accept` / `decision` land on `success` (the
   * verdict is positive); `reissue` is `info` (a neutral retry);
   * `escalate` is `warning`; `giveup` is `error`. Unknown topics fall
   * back to `accent` so a never-seen topic still renders with a
   * distinctive border instead of looking like a benign info note.
   */
  kindFor(topic: string): NotificationKind {
    switch (topic) {
      case 'accept':
      case 'accept-as-done':
      case 'decision':  return 'success';
      case 'reissue':   return 'info';
      case 'escalate':  return 'warning';
      case 'giveup':    return 'error';
      default:          return 'accent';
    }
  }

  /**
   * Operator-facing headline for the banner. The backend now writes a
   * single coherent sentence into the chat-log body (e.g.
   * `Auto-review accepted "Title" as done. Moved to 5-human-review for
   * your approval. Reason: ...`); the banner renders that summary
   * verbatim instead of stitching verb + slug. Falls back to a short
   * topic-based phrase when the message did not carry a summary (older
   * messages, malformed bus records). Includes the task title or slug
   * so the headline always names what the verdict applies to.
   */
  headlineFor(msg: DisplayDecision): string {
    if (msg.summary && msg.summary.trim().length > 0) return msg.summary;
    const target = msg.jobId || '(unknown task)';
    switch (msg.topic) {
      case 'reissue':        return `Auto-review sent "${target}" back to ready for another attempt.`;
      case 'escalate':       return `Auto-review escalated "${target}" for human attention.`;
      case 'decision':
      case 'accept':
      case 'accept-as-done': return `Auto-review accepted "${target}" as done. Waiting for your approval in human review.`;
      case 'giveup':         return `Auto-review gave up on "${target}".`;
      default:               return `Auto-review verdict for "${target}".`;
    }
  }

  private pollNow(): void {
    const projects = this.projects();
    if (!projects.length) return;
    let bestSoFar: DisplayDecision | null = this.current();
    let pending = projects.length;
    for (const project of projects) {
      const url = `/api/bus/${encodeURIComponent(project)}/messages?kind=decision&tag=orchestrator-chat&limit=5`;
      this.http.get<BusDecisionMessage[]>(url).subscribe({
        next: items => {
          for (const m of items ?? []) {
            const created = Date.parse(m.createdAt);
            if (isNaN(created)) continue;
            if (Date.now() - created > MIN_DISPLAY_MS) continue;
            const topic = (m.topic ?? '').toLowerCase() || 'decision';
            // Skip watchdog state notes (silence warnings) that share the
            // orchestrator-chat stream. Only auto-review verdicts belong
            // on the workspace banner.
            if (!BANNER_TOPICS.has(topic)) continue;
            if (!bestSoFar || created > bestSoFar.createdAt) {
              bestSoFar = {
                id: m.id,
                createdAt: created,
                topic,
                jobId: m.jobId ?? '(unknown job)',
                project: m.project ?? project,
                summary: m.summary ?? ''
              };
            }
          }
          if (--pending === 0 && bestSoFar) {
            const cur = this.current();
            if (!cur || bestSoFar.createdAt > cur.createdAt) {
              this.current.set(bestSoFar);
              if (this.dismissedId() !== bestSoFar.id) this.dismissedId.set(null);
            }
          }
        },
        error: () => { pending--; }
      });
    }
  }
}
