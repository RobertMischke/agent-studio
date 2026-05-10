import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

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

@Component({
  selector: 'app-workspace-banner',
  standalone: true,
  template: `
    @if (displayed(); as msg) {
      <div class="banner"
           [class]="'banner--' + msg.topic"
           role="status"
           data-testid="workspace-banner">
        <span class="banner__icon" aria-hidden="true">{{ iconFor(msg.topic) }}</span>
        <span class="banner__text">
          Orchestrator decided <strong>{{ verbFor(msg.topic) }}</strong>
          for <strong>{{ msg.jobId }}</strong>
          <span class="banner__project">in {{ msg.project }}</span>
        </span>
        @if (msg.summary) {
          <span class="banner__summary">{{ msg.summary }}</span>
        }
        <button type="button"
                class="banner__close"
                aria-label="Dismiss"
                data-testid="workspace-banner-close"
                (click)="dismiss()">×</button>
      </div>
    }
  `,
  styles: [`
    .banner {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 12px;
      border-radius: 10px;
      font-size: 13px;
      color: #f1f5f9;
      background: rgba(139, 92, 246, 0.18);
      border: 1px solid rgba(139, 92, 246, 0.42);
      box-shadow: 0 4px 18px rgba(139, 92, 246, 0.18);
      animation: banner-slide-in 0.18s ease-out;
    }
    .banner--reissue   { background: rgba(56, 189, 248, 0.18); border-color: rgba(56, 189, 248, 0.45); }
    .banner--escalate  { background: rgba(252, 211, 77, 0.18); border-color: rgba(252, 211, 77, 0.45); color: #fde68a; }
    .banner--giveup    { background: rgba(244, 63, 94, 0.18);  border-color: rgba(244, 63, 94, 0.45);  color: #fda4af; }
    .banner__icon { font-size: 16px; line-height: 1; }
    .banner__text strong { color: #fff; }
    .banner__project { color: rgba(255,255,255,0.72); margin-left: 4px; }
    .banner__summary {
      color: rgba(255,255,255,0.78);
      flex: 1;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .banner__close {
      background: transparent;
      border: 0;
      color: rgba(255,255,255,0.7);
      font-size: 18px;
      line-height: 1;
      cursor: pointer;
      padding: 0 4px;
    }
    .banner__close:hover { color: #fff; }
    @keyframes banner-slide-in {
      from { opacity: 0; transform: translateY(-4px); }
      to   { opacity: 1; transform: translateY(0); }
    }
  `]
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

  /** Topic-to-verb mapping for the banner copy ("Orchestrator decided X for Y"). */
  verbFor(topic: string): string {
    switch (topic) {
      case 'reissue':   return 'reissue';
      case 'escalate':  return 'escalate';
      case 'decision':  return 'accept';
      case 'giveup':    return 'give up';
      case 'heuristic': return 'use heuristic';
      default:          return topic;
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
            if (!bestSoFar || created > bestSoFar.createdAt) {
              bestSoFar = {
                id: m.id,
                createdAt: created,
                topic: (m.topic ?? '').toLowerCase() || 'decision',
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
