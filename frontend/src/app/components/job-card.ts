import { Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { AutoLoopSnapshot, JobInfo, JobTokenSummary, PendingIntent } from '../models/job.model';
import { GitSummaryService } from '../services/git-summary.service';
import { ClientService } from '../services/client.service';
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
         [class.job-card--compact]="compact()"
         [style.--project-color]="identity().color"
         [style.--project-border]="identity().border"
         [style.--project-soft]="identity().soft"
         [style.--project-on]="identity().onColor"
         data-testid="job-card"
         [attr.data-project]="job().projectName"
         [attr.data-running]="isRunning() ? 'true' : null"
         [attr.data-compact]="compact() ? 'true' : null">
      <div class="job-card__header">
        <span class="job-card__project" data-testid="job-card-project">
          <span class="job-card__project-disk" aria-hidden="true">{{ identity().initial }}</span>
          <span class="job-card__project-name">{{ job().projectName }}</span>
        </span>
        @if (ownerChip(); as oc) {
          <span class="job-card__owner-chip"
                data-testid="job-card-owner"
                [attr.data-owner-id]="oc.id"
                [style.background]="oc.background"
                [style.borderColor]="oc.border"
                [style.color]="oc.foreground"
                [title]="oc.tooltip">
            <span class="job-card__owner-emoji" aria-hidden="true">{{ oc.emoji }}</span>
            <span class="job-card__owner-name">{{ oc.label }}</span>
          </span>
        }
        <span class="job-card__order">#{{ job().order }}</span>
        <button type="button"
                class="job-card__delete"
                data-testid="job-card-delete"
                title="Delete task"
                aria-label="Delete task"
                (click)="onDeleteClick($event)">🗑</button>
      </div>
      <h3 class="job-card__title">
        @if (compact()) {
          <span class="job-card__compact-disk"
                [attr.aria-label]="job().projectName"
                aria-hidden="true">{{ identity().initial }}</span>
          <span class="job-card__compact-cli" aria-hidden="true">{{ agentIcon() }}</span>
        }
        <span class="job-card__title-text">{{ job().title || job().id }}</span>
        @if (compact()) {
          <span class="job-card__compact-time" [title]="'Last activity: ' + relativeActivity()">
            {{ relativeActivity() }}
          </span>
        }
      </h3>
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
        @if (job().autoLoop; as al) {
          <span class="job-card__loop-pill"
                [class.job-card__loop-pill--hot]="loopHot()"
                [title]="loopTooltip(al)"
                data-testid="job-card-autoloop">
            ↻ auto-loop {{ al.iteration }}/{{ al.maxIterations }}
          </span>
        }
        @if (reviewBadge(); as rb) {
          <span class="job-card__review-pill"
                [class]="'job-card__review-pill--' + rb.tone"
                [title]="rb.tooltip"
                data-testid="job-card-review">
            <span class="job-card__review-dot"></span>
            {{ rb.label }}
          </span>
        }
      </div>
      <div class="job-card__meta">
        <span class="job-card__agent">{{ agentIcon() }} {{ job().agent || 'unknown' }}</span>
        @if (job().model) {
          <span class="job-card__model">🧠 {{ job().model }}</span>
        }
        @if (tokenBubble(); as tb) {
          <span class="job-card__token-wrap">
            <button type="button"
                    class="job-card__token-bubble"
                    [class]="'job-card__token-bubble--' + tb.tier"
                    data-testid="job-card-token-bubble"
                    [attr.data-token-tier]="tb.tier"
                    [attr.data-token-total]="tb.total"
                    [attr.aria-label]="'Token usage: ' + tb.label + ' total tokens'"
                    (click)="$event.stopPropagation()">
              {{ tb.label }}
            </button>
            <span class="job-card__token-popover"
                  role="tooltip"
                  data-testid="job-card-token-popover">
              <span class="job-card__token-popover-title">Token usage</span>
              <table class="job-card__token-table">
                <tbody>
                  <tr><th>Input</th><td data-testid="token-row-input">{{ formatTokens(tb.input) }}</td></tr>
                  <tr><th>Output</th><td data-testid="token-row-output">{{ formatTokens(tb.output) }}</td></tr>
                  <tr><th>Cache read</th><td data-testid="token-row-cache-read">{{ formatTokens(tb.cacheRead) }}</td></tr>
                  <tr><th>Cache write</th><td data-testid="token-row-cache-write">{{ formatTokens(tb.cacheWrite) }}</td></tr>
                  <tr class="job-card__token-total-row"><th>Total</th><td data-testid="token-row-total">{{ formatTokens(tb.total) }}</td></tr>
                  <tr><th>Model</th><td data-testid="token-row-model">{{ tb.model || '-' }}</td></tr>
                  <tr><th>Last update</th><td data-testid="token-row-last-update">{{ tb.lastUpdate || '-' }}</td></tr>
                </tbody>
              </table>
              @if (tb.entries.length > 1) {
                <div class="job-card__token-runs-title">Per-run</div>
                <table class="job-card__token-table job-card__token-table--runs">
                  <thead>
                    <tr><th>When</th><th>Model</th><th>Tokens</th></tr>
                  </thead>
                  <tbody>
                    @for (row of tb.entries; track row.ts) {
                      <tr>
                        <td>{{ row.tsLabel }}</td>
                        <td>{{ row.model || '-' }}</td>
                        <td>{{ formatTokens(row.total) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
              <a class="job-card__token-link"
                 href="/workspace/tokens"
                 data-testid="token-popover-timeline-link"
                 (click)="$event.stopPropagation()">
                View workspace timeline
              </a>
            </span>
          </span>
        }
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
      /* Compositor-only transitions. transform handles hover lift and
         post-drop settle (CDK rhythm: 180ms cubic-bezier(0,0,0.2,1));
         box-shadow handles depth on hover. background-color is
         deliberately not transitioned — drag-and-drop must never
         brighten the card surface. (Motion rule, design-principles.md) */
      transition:
        transform 180ms cubic-bezier(0, 0, 0.2, 1),
        box-shadow 0.15s ease,
        opacity 0.18s ease;
      border-left: 4px solid var(--state-color, #555);
    }
    /* Drag-source: while a card is being dragged, dim it to ~55% so the
       drag-image (browser-rendered ghost) reads as the active object.
       The transition above handles the smooth restore on release. */
    :host(.drag-source) .job-card {
      opacity: 0.55;
      will-change: transform, opacity;
    }
    @media (prefers-reduced-motion: reduce) {
      .job-card { transition: none; }
      .job-card--running { animation: none; }
      :host(.drag-source) .job-card { will-change: auto; }
    }
    .job-card:hover {
      transform: translateY(-2px);
      box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    }
    .job-card--1-preparation { --state-color: #8b5cf6; }
    .job-card--2-ready { --state-color: #06b6d4; }
    .job-card--3-progress { --state-color: #3b82f6; }
    /* ADR-0025: distinct accents for the two review lanes - amber for the
       orchestrator's machine pass, sky for human-attention. The legacy
       4-review class is kept for any data still rendering the old name. */
    .job-card--4-review { --state-color: #f59e0b; }
    .job-card--4-auto-review { --state-color: #f59e0b; }
    .job-card--5-human-review { --state-color: #38bdf8; }
    .job-card--5-completed { --state-color: #10b981; }
    .job-card--6-completed { --state-color: #10b981; }

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
    .job-card__owner-chip {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 8px 2px 4px;
      border-radius: 999px;
      font-size: 11px;
      font-weight: 600;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cbd5e1;
      max-width: 140px;
      overflow: hidden;
    }
    .job-card__owner-emoji {
      font-size: 12px;
      line-height: 1;
    }
    .job-card__owner-name {
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
    /* Delete affordance: hidden until card hover so the cards stay calm in
       the default board view. The button stops propagation so clicking it
       never opens the detail panel. The confirmation prompt itself lives
       in the parent component. */
    .job-card__delete {
      background: transparent;
      border: 1px solid transparent;
      color: #64748b;
      width: 22px;
      height: 22px;
      padding: 0;
      border-radius: 6px;
      cursor: pointer;
      font-size: 13px;
      line-height: 1;
      display: grid;
      place-items: center;
      opacity: 0;
      transition: opacity 0.15s, background 0.15s, color 0.15s, border-color 0.15s;
    }
    .job-card:hover .job-card__delete,
    .job-card__delete:focus-visible {
      opacity: 1;
    }
    .job-card__delete:hover {
      background: rgba(244, 63, 94, 0.15);
      border-color: rgba(244, 63, 94, 0.40);
      color: #fda4af;
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
    /* Auto-loop pill: shown only while the orchestrator is actively
       answering NEEDS_INPUT for this job. Cyan when within budget;
       turns amber as the iteration counter approaches the cap so the
       user can see the loop heading toward the circuit breaker. */
    .job-card__loop-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 8px;
      border-radius: 999px;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.02em;
      color: #a5f3fc;
      background: rgba(165, 243, 252, 0.10);
      border: 1px solid rgba(165, 243, 252, 0.28);
      cursor: help;
    }
    .job-card__loop-pill--hot {
      color: #fde68a;
      background: rgba(253, 230, 138, 0.12);
      border-color: rgba(253, 230, 138, 0.32);
    }
    /* Auto-review pill: shown while the post-completion summarizer is
       still running on a card that just landed in 4-review (amber, with
       a pulsing dot to mirror the running execution pill), or briefly
       after it finishes/fails so the user sees the result. Mirrors the
       visual vocabulary of the execution pill so "something is happening
       on this card" reads consistently across lanes. */
    .job-card__review-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 8px;
      border-radius: 999px;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.02em;
      border: 1px solid transparent;
      cursor: help;
    }
    .job-card__review-dot {
      width: 7px;
      height: 7px;
      border-radius: 999px;
      background: currentColor;
      flex: 0 0 auto;
    }
    .job-card__review-pill--generating {
      color: #fcd34d;
      background: rgba(252, 211, 77, 0.14);
      border-color: rgba(252, 211, 77, 0.32);
    }
    .job-card__review-pill--generating .job-card__review-dot {
      animation: pulse-running 1.3s infinite;
    }
    .job-card__review-pill--ready {
      color: #86efac;
      background: rgba(134, 239, 172, 0.12);
      border-color: rgba(134, 239, 172, 0.28);
    }
    .job-card__review-pill--failed {
      color: #fda4af;
      background: rgba(244, 63, 94, 0.14);
      border-color: rgba(244, 63, 94, 0.25);
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

    /* Token bubble: a small status indicator that shows total tokens spent
       on this task. The popover opens on hover and on keyboard focus, so
       the bubble is keyboard-reachable as well. We render it as a button
       (not a div) so screen readers and Tab navigation pick it up. */
    .job-card__token-wrap {
      position: relative;
      display: inline-flex;
      align-items: center;
    }
    .job-card__token-bubble {
      min-width: 28px;
      height: 18px;
      padding: 0 6px;
      border-radius: 999px;
      border: 1px solid rgba(255,255,255,0.18);
      display: inline-grid;
      place-items: center;
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.02em;
      cursor: help;
      color: #e2e8f0;
      background: rgba(148,163,184,0.18);
      font-variant-numeric: tabular-nums;
    }
    .job-card__token-bubble:focus-visible {
      outline: 2px solid #7dd3fc;
      outline-offset: 2px;
    }
    /* Spend-tier colours. Defaults: < 50k = neutral, 50k-500k = blue,
       500k-5M = mauve, > 5M = peach. Tier thresholds are computed in the
       component (see tokenBubble()). Catppuccin palette to stay in line
       with the rest of the board. */
    .job-card__token-bubble--neutral {
      color: #cbd5e1;
      background: rgba(148,163,184,0.18);
      border-color: rgba(148,163,184,0.32);
    }
    .job-card__token-bubble--blue {
      color: #bae6fd;
      background: rgba(56,189,248,0.18);
      border-color: rgba(56,189,248,0.40);
    }
    .job-card__token-bubble--mauve {
      color: #e9d5ff;
      background: rgba(192,132,252,0.20);
      border-color: rgba(192,132,252,0.42);
    }
    .job-card__token-bubble--peach {
      color: #fed7aa;
      background: rgba(251,146,60,0.22);
      border-color: rgba(251,146,60,0.45);
    }

    .job-card__token-popover {
      display: none;
      position: absolute;
      bottom: calc(100% + 8px);
      right: 0;
      z-index: 30;
      min-width: 240px;
      max-width: 320px;
      padding: 10px 12px;
      border-radius: 8px;
      background: #11131a;
      border: 1px solid rgba(255,255,255,0.10);
      box-shadow: 0 8px 28px rgba(0,0,0,0.45);
      color: #e2e8f0;
      text-align: left;
      pointer-events: none;
    }
    .job-card__token-wrap:hover .job-card__token-popover,
    .job-card__token-bubble:focus-visible + .job-card__token-popover,
    .job-card__token-wrap:focus-within .job-card__token-popover {
      display: block;
      pointer-events: auto;
    }
    .job-card__token-popover-title {
      display: block;
      font-size: 11px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: #94a3b8;
      margin-bottom: 6px;
    }
    .job-card__token-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 11px;
    }
    .job-card__token-table th {
      text-align: left;
      font-weight: 500;
      color: #94a3b8;
      padding: 2px 8px 2px 0;
    }
    .job-card__token-table td {
      text-align: right;
      font-variant-numeric: tabular-nums;
      color: #e2e8f0;
      padding: 2px 0;
    }
    .job-card__token-total-row th,
    .job-card__token-total-row td {
      border-top: 1px solid rgba(255,255,255,0.08);
      padding-top: 4px;
      font-weight: 700;
    }
    .job-card__token-runs-title {
      margin-top: 8px;
      font-size: 11px;
      font-weight: 700;
      color: #94a3b8;
    }
    .job-card__token-table--runs th,
    .job-card__token-table--runs td {
      font-size: 10px;
      padding: 2px 6px 2px 0;
    }
    .job-card__token-link {
      display: inline-block;
      margin-top: 8px;
      font-size: 11px;
      color: #7dd3fc;
      text-decoration: none;
    }
    .job-card__token-link:hover { text-decoration: underline; }

    /* Compact-card mode: collapse the card to a single dense row showing
       only what is needed to find a task by name. CLI icon + project
       initial sit before the title; a relative timestamp sits at the end.
       Everything else (header chips, badges, meta, git/commit pills,
       activity line) is hidden so the user can fit many more cards on
       screen. The drag-handle, click-to-open behaviour, and per-state
       border accent are preserved. */
    .job-card--compact {
      padding: 6px 10px;
      border-radius: 8px;
    }
    .job-card--compact .job-card__header,
    .job-card--compact .job-card__badges,
    .job-card--compact .job-card__meta,
    .job-card--compact .job-card__git,
    .job-card--compact .job-card__commit,
    .job-card--compact .job-card__activity {
      display: none;
    }
    .job-card--compact .job-card__title {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0;
      font-size: 13px;
      font-weight: 500;
      line-height: 1.25;
      min-width: 0;
    }
    .job-card--compact .job-card__title-text {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .job-card--compact .job-card__compact-disk {
      display: inline-grid;
      place-items: center;
      width: 16px;
      height: 16px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 9px;
      font-weight: 800;
      flex: 0 0 auto;
    }
    .job-card--compact .job-card__compact-cli {
      font-size: 12px;
      line-height: 1;
      flex: 0 0 auto;
    }
    .job-card--compact .job-card__compact-time {
      font-size: 10px;
      color: #64748b;
      font-variant-numeric: tabular-nums;
      flex: 0 0 auto;
      white-space: nowrap;
    }
    /* In compact mode the running pulse animation would move every row in
       a busy column; replace it with a steady accent so the eye is drawn
       to the running card without a constantly-shifting background. */
    .job-card--compact.job-card--running {
      animation: none;
      background:
        linear-gradient(180deg, rgba(59,130,246,0.16), rgba(59,130,246,0.04)),
        #1e1e2e;
    }
  `]
})
export class JobCardComponent implements OnInit, OnDestroy {
  readonly job = input.required<JobInfo>();
  readonly compact = input<boolean>(false);
  readonly deleteRequested = output<JobInfo>();
  private readonly gitSummary = inject(GitSummaryService);
  private readonly clients = inject(ClientService);
  private stopPolling: (() => void) | null = null;

  onDeleteClick(event: MouseEvent) {
    event.stopPropagation();
    this.deleteRequested.emit(this.job());
  }

  /**
   * Owner-attribution chip on every card. Resolves the job's
   * `ownerClientId` against the registry from /api/clients and renders
   * emoji + display name + the owner's chosen colour. Falls back to a
   * neutral placeholder when the registry has not loaded yet.
   */
  readonly ownerChip = computed<{
    id: string;
    label: string;
    emoji: string;
    background: string;
    border: string;
    foreground: string;
    tooltip: string;
  } | null>(() => {
    const ownerId = this.job().ownerClientId;
    if (!ownerId) return null;
    const c = this.clients.resolve(ownerId);
    const baseColour = c.colour || '#64748b';
    return {
      id: c.id,
      label: c.displayName || c.id,
      emoji: c.emoji || '·',
      background: this.tintFromHex(baseColour, 0.12),
      border: this.tintFromHex(baseColour, 0.32),
      foreground: '#e2e8f0',
      tooltip: `Owner: ${c.displayName || c.id} (${c.id})`
    };
  });

  private tintFromHex(hex: string, alpha: number): string {
    const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
    if (!m) return `rgba(100,116,139,${alpha})`;
    let body = m[1];
    if (body.length === 3) body = body.split('').map(ch => ch + ch).join('');
    const r = parseInt(body.slice(0, 2), 16);
    const g = parseInt(body.slice(2, 4), 16);
    const b = parseInt(body.slice(4, 6), 16);
    return `rgba(${r},${g},${b},${alpha})`;
  }

  // Git status only matters for tasks the user is actively working on or
  // about to review — pre-work lanes (preparation/ready) and post-review
  // lanes (completed/archive) carry no useful per-task git context, so we
  // skip the pill there to keep the board calm.
  // ADR-0025: pill stays in both review lanes (auto + human).
  private static readonly LANES_WITH_GIT = new Set([
    '3-progress', '4-auto-review', '5-human-review', '4-review',
  ]);

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

    // 'stopped' is the new deliberate-kill status from the backend
    // (user pause, Pause-&-Send, watchdog kill). Render as a calm
    // "Stopped" pill, not a failure. Legacy 'cancelled' value stays
    // supported so older in-memory CliExecution records keep rendering.
    if (execution.status === 'stopped' || execution.status === 'cancelled') {
      return { label: 'Stopped', tone: 'cancelled' };
    }

    return null;
  }

  /**
   * Review-pill descriptor: shows the auto-review (Haiku summarizer)
   * status on a card that landed in 4-auto-review. Returns null when
   * there is nothing to show (no run, or the user already moved on).
   */
  readonly reviewBadge = computed<{ label: string; tone: 'generating' | 'ready' | 'failed'; tooltip: string } | null>(() => {
    const s = this.job().summaryState;
    if (!s) return null;
    switch (s.status) {
      case 'generating':
        return { label: 'auto-reviewing', tone: 'generating',
                 tooltip: 'Orchestrator is summarizing the run output (Haiku). The card will become quiet once status.md has been written.' };
      case 'ready':
        return { label: 'review ready', tone: 'ready',
                 tooltip: s.bytesWritten ? `Auto-review wrote ${s.bytesWritten} bytes to status.md.` : 'Auto-review finished.' };
      case 'failed':
        return { label: 'review failed', tone: 'failed',
                 tooltip: s.errorMessage ?? 'Auto-review failed.' };
      default:
        return null;
    }
  });

  /** Hot-state threshold: amber pill once the loop is at 80% of the iteration cap. */
  readonly loopHot = computed(() => {
    const al = this.job().autoLoop;
    if (!al || al.maxIterations <= 0) return false;
    return al.iteration / al.maxIterations >= 0.8;
  });

  loopTooltip(al: AutoLoopSnapshot): string {
    const tokenLine = `${al.tokensUsed.toLocaleString()} / ${al.maxTokens.toLocaleString()} orchestrator tokens`;
    const startedAt = (() => { try { return new Date(al.startedAt).toLocaleString(); } catch { return al.startedAt; } })();
    const lastQ = (al.lastQuestion ?? '').slice(0, 160);
    const lastErr = al.lastError ? `\nLast error: ${al.lastError}` : '';
    return `Auto-loop: orchestrator answering NEEDS_INPUT for this job.\n` +
           `Iteration ${al.iteration} of ${al.maxIterations}.\n` +
           `${tokenLine}.\nStarted ${startedAt}.${lastErr}\n\nLast question: ${lastQ}${(al.lastQuestion ?? '').length > 160 ? '...' : ''}`;
  }

  pendingTooltip(pi: PendingIntent): string {
    const when = (() => {
      try { return new Date(pi.savedAt).toLocaleString(); }
      catch { return pi.savedAt; }
    })();
    const preview = (pi.prompt ?? '').slice(0, 120);
    return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
  }

  /** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
  formatTokens(n: number): string {
    if (!isFinite(n) || n <= 0) return '0';
    if (n < 1000) return Math.round(n).toString();
    if (n < 1_000_000) {
      const k = n / 1000;
      return (k >= 100 ? Math.round(k) : Number(k.toFixed(1))) + 'k';
    }
    const m = n / 1_000_000;
    return (m >= 100 ? Math.round(m) : Number(m.toFixed(1))) + 'M';
  }

  /**
   * Token-bubble descriptor: returns null when the task has no recorded
   * orchestrator activity (input + output + cacheRead + cacheWrite == 0).
   * Tier thresholds match the prompt: < 50k neutral, < 500k blue,
   * < 5M mauve, otherwise peach.
   */
  readonly tokenBubble = computed<{
    label: string;
    total: number;
    input: number;
    output: number;
    cacheRead: number;
    cacheWrite: number;
    model: string | null;
    lastUpdate: string | null;
    tier: 'neutral' | 'blue' | 'mauve' | 'peach';
    entries: { ts: string; tsLabel: string; model: string | null; total: number }[];
  } | null>(() => {
    const t = this.job().tokenSummary;
    if (!t) return null;
    const input = t.inputTokens ?? 0;
    const output = t.outputTokens ?? 0;
    const cacheRead = t.cacheReadTokens ?? 0;
    const cacheWrite = t.cacheCreationTokens ?? 0;
    const total = input + output + cacheRead + cacheWrite;
    if (total <= 0) return null;
    const tier = total >= 5_000_000 ? 'peach'
      : total >= 500_000 ? 'mauve'
      : total >= 50_000 ? 'blue'
      : 'neutral';
    const entries = (t.entries ?? []).map(e => ({
      ts: e.ts,
      tsLabel: this.formatShortTime(e.ts),
      model: e.model,
      total: (e.inputTokens ?? 0) + (e.outputTokens ?? 0) + (e.cacheReadTokens ?? 0) + (e.cacheCreationTokens ?? 0)
    }));
    return {
      label: this.formatTokens(total),
      total,
      input,
      output,
      cacheRead,
      cacheWrite,
      model: t.lastModel ?? null,
      lastUpdate: t.lastUpdate ? this.formatShortTime(t.lastUpdate) : null,
      tier,
      entries
    };
  });

  private formatShortTime(iso: string): string {
    try {
      return new Date(iso).toLocaleString();
    } catch {
      return iso;
    }
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
