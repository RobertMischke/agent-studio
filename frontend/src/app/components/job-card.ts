import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { JobInfo, PendingIntent } from '../models/job.model';
import { GitSummaryService } from '../services/git-summary.service';
import { cliTypeIcon } from '../services/format.util';
import { projectIdentity } from '../services/project-identity.util';

// Shared 'now' signal that ticks every 30s so all relative timestamps update in lockstep
// without re-reading Date.now() during change detection (which causes NG0100).
const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-job-card',
  standalone: true,
  template: `
    <div class="job-card"
         [class]="'job-card--' + job().state"
         [class.job-card--running]="isRunning()"
         [style.--project-color]="identity().color"
         [style.--project-border]="identity().border"
         [style.--project-soft]="identity().soft"
         [style.--project-on]="identity().onColor"
         data-testid="job-card"
         [attr.data-project]="job().projectName"
         [attr.data-running]="isRunning() ? 'true' : null">
      <div class="job-card__header">
        <span class="job-card__project" data-testid="job-card-project">
          <span class="job-card__project-disk" aria-hidden="true">{{ identity().initial }}</span>
          <span class="job-card__project-name">{{ job().projectName }}</span>
        </span>
        <span class="job-card__order">#{{ job().order }}</span>
      </div>
      <h3 class="job-card__title">{{ job().title || job().id }}</h3>
      <div class="job-card__badges">
        <span class="job-card__state-pill">{{ stateLabel() }}</span>
        @if (executionBadge(); as badge) {
          <span class="job-card__execution-pill" [class]="'job-card__execution-pill--' + badge.tone">
            <span class="job-card__execution-dot"></span>
            {{ badge.label }}
          </span>
        }
        @if (job().pendingIntent; as pi) {
          <span class="job-card__pending-pill"
                [title]="pendingTooltip(pi)"
                data-testid="job-card-pending">
            ⏳ {{ pi.mode }}
          </span>
        }
      </div>
      <div class="job-card__meta">
        <span class="job-card__agent">{{ agentIcon() }} {{ job().agent || 'unknown' }}</span>
        @if (job().model) {
          <span class="job-card__model">🧠 {{ job().model }}</span>
        }
        <span class="job-card__size">{{ formatSize(job().totalSizeBytes) }}</span>
      </div>
      @if (gitPill(); as g) {
        <div class="job-card__git" [title]="gitTooltip()" data-testid="job-card-git">
          <span class="job-card__git-branch">⎇ {{ g.branch || '?' }}</span>
          <span class="job-card__git-count" [class.job-card__git-count--clean]="g.filesChanged === 0">
            {{ g.filesChanged }} {{ g.filesChanged === 1 ? 'file' : 'files' }}
          </span>
          @if (g.totalAdded || g.totalRemoved) {
            <span class="job-card__git-stat">+{{ g.totalAdded }}/−{{ g.totalRemoved }}</span>
          }
        </div>
      }
      @if (job().commit; as c) {
        <div class="job-card__commit" [title]="commitTooltip()" data-testid="job-card-commit">
          <span class="job-card__commit-sha">⏺ {{ c.shortSha }}</span>
          <span class="job-card__commit-files">{{ c.filesChanged }} {{ c.filesChanged === 1 ? 'file' : 'files' }}</span>
        </div>
      }
      <div class="job-card__activity">
        Last activity: {{ relativeActivity() }}
      </div>
    </div>
  `,
  styles: [`
    .job-card {
      background: var(--card-bg, #1e1e2e);
      border: 1px solid var(--border, #333);
      border-radius: 12px;
      padding: 16px;
      cursor: pointer;
      transition: transform 0.15s, box-shadow 0.15s;
      border-left: 4px solid var(--state-color, #555);
    }
    .job-card:hover {
      transform: translateY(-2px);
      box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    }
    .job-card--1-preparation { --state-color: #8b5cf6; }
    .job-card--2-ready { --state-color: #06b6d4; }
    .job-card--3-progress { --state-color: #3b82f6; }
    .job-card--4-review { --state-color: #f59e0b; }
    .job-card--5-completed { --state-color: #10b981; }

    /* Running tasks should jump out of the column. We brighten the surface,
       widen the state accent, and add a slow breathing glow so the eye is
       drawn to whatever is happening *right now* without constant motion
       fatigue. */
    .job-card--running {
      background:
        linear-gradient(180deg, rgba(59,130,246,0.16), rgba(59,130,246,0.04)) ,
        #1e1e2e;
      border-color: rgba(59,130,246,0.45);
      border-left-width: 6px;
      box-shadow:
        0 0 0 1px rgba(59,130,246,0.18),
        0 8px 22px rgba(59,130,246,0.20);
      animation: job-running-glow 2.4s ease-in-out infinite;
    }
    .job-card--running:hover {
      transform: translateY(-2px);
      box-shadow:
        0 0 0 1px rgba(59,130,246,0.30),
        0 14px 32px rgba(59,130,246,0.30);
    }
    @keyframes job-running-glow {
      0%, 100% {
        box-shadow:
          0 0 0 1px rgba(59,130,246,0.18),
          0 8px 22px rgba(59,130,246,0.18);
      }
      50% {
        box-shadow:
          0 0 0 1px rgba(96,165,250,0.40),
          0 14px 36px rgba(59,130,246,0.32);
      }
    }

    .job-card__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }
    .job-card__project {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--project-color, #8b5cf6);
      font-weight: 600;
      background: var(--project-soft, rgba(139,92,246,0.10));
      border: 1px solid var(--project-border, transparent);
      padding: 2px 8px 2px 3px;
      border-radius: 999px;
      max-width: 100%;
      overflow: hidden;
    }
    .job-card__project-disk {
      display: inline-grid;
      place-items: center;
      width: 16px;
      height: 16px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 10px;
      font-weight: 800;
      letter-spacing: 0;
      flex: 0 0 auto;
    }
    .job-card__project-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      min-width: 0;
    }
    .job-card__order {
      font-size: 11px;
      padding: 2px 6px;
      border-radius: 4px;
      background: rgba(255,255,255,0.08);
      color: #94a3b8;
      font-weight: 600;
      font-variant-numeric: tabular-nums;
    }

    .job-card__title {
      margin: 0 0 8px;
      font-size: 15px;
      font-weight: 600;
      color: #e2e8f0;
    }
    .job-card__badges {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-bottom: 10px;
    }
    .job-card__state-pill,
    .job-card__execution-pill,
    .job-card__pending-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 8px;
      border-radius: 999px;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    .job-card__pending-pill {
      color: #f9e2af;
      background: rgba(249, 226, 175, 0.12);
      border: 1px solid rgba(249, 226, 175, 0.28);
      cursor: help;
    }
    .job-card__state-pill {
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
    }
    .job-card__execution-pill {
      border: 1px solid transparent;
    }
    .job-card__execution-dot {
      width: 7px;
      height: 7px;
      border-radius: 999px;
      background: currentColor;
      flex: 0 0 auto;
    }
    .job-card__execution-pill--running {
      color: #7dd3fc;
      background: rgba(14, 165, 233, 0.14);
      border-color: rgba(14, 165, 233, 0.25);
    }
    .job-card__execution-pill--running .job-card__execution-dot {
      animation: pulse-running 1.3s infinite;
    }
    .job-card__execution-pill--failed,
    .job-card__execution-pill--cancelled {
      color: #fda4af;
      background: rgba(244, 63, 94, 0.14);
      border-color: rgba(244, 63, 94, 0.25);
    }
    @keyframes pulse-running {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.35; }
    }
    .job-card__meta {
      display: flex;
      justify-content: space-between;
      gap: 6px;
      flex-wrap: wrap;
      font-size: 12px;
      color: #94a3b8;
      margin-bottom: 4px;
    }
    .job-card__model {
      color: #c4b5fd;
      font-family: var(--font-mono, monospace);
      font-size: 11px;
    }
    .job-card__activity {
      font-size: 11px;
      color: #64748b;
    }
    .job-card__git {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 8px;
      margin: 2px 0 6px;
      border-radius: 999px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.06);
      font-size: 11px;
      color: #cbd5e1;
    }
    .job-card__git-branch { color: #a5b4fc; font-family: var(--font-mono, monospace); }
    .job-card__git-count { color: #fbbf24; font-weight: 600; }
    .job-card__git-count--clean { color: #86efac; }
    .job-card__git-stat { color: #94a3b8; font-family: var(--font-mono, monospace); font-size: 10px; }
    .job-card__commit {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 8px;
      margin: 0 0 6px;
      border-radius: 999px;
      background: rgba(16, 185, 129, 0.10);
      border: 1px solid rgba(16, 185, 129, 0.25);
      font-size: 11px;
      color: #86efac;
    }
    .job-card__commit-sha { font-family: var(--font-mono, monospace); font-weight: 600; }
    .job-card__commit-files { color: #94a3b8; }
  `]
})
export class JobCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<JobInfo>();
  private readonly gitSummary = inject(GitSummaryService);
  private stopPolling: (() => void) | null = null;

  // Git status only matters for tasks the user is actively working on or
  // about to review — pre-work lanes (preparation/ready) and post-review
  // lanes (completed/archive) carry no useful per-task git context, so we
  // skip the pill there to keep the board calm.
  private static readonly LANES_WITH_GIT = new Set(['3-progress', '4-review']);

  readonly gitPill = computed(() => {
    if (!JobCardComponent.LANES_WITH_GIT.has(this.job().state)) return null;
    const projectName = this.job().projectName;
    const summary = this.gitSummary.value().find(s => s.projectName === projectName);
    return summary && summary.isRepo ? summary : null;
  });

  readonly gitTooltip = computed(() => {
    const g = this.gitPill();
    if (!g) return '';
    return `Branch: ${g.branch ?? '(detached)'}\n${g.filesChanged} changed file(s) in ${g.rootPath}\n+${g.totalAdded} / −${g.totalRemoved}`;
  });

  readonly commitTooltip = computed(() => {
    const c = this.job().commit;
    if (!c) return '';
    const subject = (c.message || '').split('\n')[0];
    return `${c.shortSha} — ${subject}\n${c.filesChanged} file(s) changed`;
  });

  ngOnInit(): void { this.stopPolling = this.gitSummary.ensurePolling(); }
  ngOnDestroy(): void { this.stopPolling?.(); }

  stateLabel(): string {
    const state = this.job().state;
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }

  executionBadge(): { label: string; tone: 'running' | 'failed' | 'cancelled' } | null {
    const execution = this.job().execution;
    if (!execution) return null;

    if (execution.status === 'running') {
      return { label: 'Running live', tone: 'running' };
    }

    if (execution.status === 'failed') {
      return { label: execution.exitCode === null ? 'Failed' : `Failed (${execution.exitCode})`, tone: 'failed' };
    }

    if (execution.status === 'cancelled') {
      return { label: 'Stopped', tone: 'cancelled' };
    }

    return null;
  }

  pendingTooltip(pi: PendingIntent): string {
    const when = (() => {
      try { return new Date(pi.savedAt).toLocaleString(); }
      catch { return pi.savedAt; }
    })();
    const preview = (pi.prompt ?? '').slice(0, 120);
    return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  readonly agentIcon = computed(() => {
    const t = this.job().cliType;
    return t ? cliTypeIcon(t) : '🤖';
  });

  readonly identity = computed(() => projectIdentity(this.job().projectName));

  readonly isRunning = computed(() => this.job().execution?.status === 'running');

  readonly relativeActivity = computed(() => {
    const dateStr = this.job().lastActivity;
    if (!dateStr) return 'never';
    const diff = nowTick() - new Date(dateStr).getTime();
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    const hrs = Math.floor(mins / 60);
    if (hrs < 24) return hrs + 'h ago';
    return Math.floor(hrs / 24) + 'd ago';
  });
}
