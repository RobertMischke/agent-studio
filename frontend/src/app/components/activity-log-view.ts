import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, input, output, signal, viewChild, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { CliOutputLine } from '../models/job.model';
import { copyTextToClipboard } from '../services/clipboard.util';
import {
  ActivityLogGroup,
  ActivityLogKind,
  ConversationTurn,
  LiveStatus,
  ParsedSteer,
  ToolBurstBin,
  activityKindLabel,
  binToolBurstByKind,
  buildConversationTurns,
  deriveLiveStatus,
  formatBurstDuration,
  formatLiveSince,
  parseActivityLog,
  parseOrchestratorSteer
} from './activity-log.parser';
import { markdownToHtml } from './markdown-utils';

type ViewMode = 'conversation' | 'trace';

interface ToolChip {
  kind: ActivityLogKind;
  /** Display name in the chip ("Read", "Grep", "Edit"). */
  label: string;
  count: number;
}

interface RenderedTurn {
  turn: ConversationTurn;
  bodyHtml: SafeHtml | null;
  /**
   * For tool bursts: per-kind chips so the reader sees "Read ×12  Grep ×5"
   * at a glance instead of a single combined sentence. Built once per turn
   * so the template doesn't re-stringify on every change-detection pass.
   */
  toolChips: ToolChip[];
  /** Compact "4s" / "1m 20s" string, or empty when the burst was effectively instant. */
  toolDuration: string;
  /** Per-kind bins for the expanded detail (lazily consumed by the template). */
  toolBins: ToolBurstBin[];
  /**
   * Set when this orchestrator turn is a [steer] message. Drives a
   * dedicated card in the conversation view: question-mark icon, the
   * one-line Need / Why ask, optional option buttons that pre-fill the
   * compose box, and a "Send screenshot" affordance when the Need
   * mentions a screenshot.
   */
  steer?: ParsedSteer;
}

/**
 * Activity Log view. The component runs in one of two modes:
 *
 * - **Conversation** (default): a chat-like read of the run. Adjacent agent
 *   text turns are joined and rendered as Markdown so the model's reply is
 *   one large readable block instead of N tiny lines. Tool calls between
 *   turns collapse to a single inline pill ("4 actions: 2 reads, 1 edit")
 *   that expands on click. User messages stand out. This is what the user
 *   reads day-to-day.
 *
 * - **Trace**: a flat chronological dump of every parsed group, useful for
 *   debugging - errors, tool detail, system frames. No per-kind filter
 *   checkboxes; a single "Show debug noise" toggle hides the truly spammy
 *   stuff (session/init frames, blank-only groups). The 9-checkbox filter
 *   row from the previous design was replaced because the user reported it
 *   created more friction than value.
 */
@Component({
  selector: 'app-activity-log-view',
  standalone: true,
  // Cycle 7b: OnPush. The activity log re-derives conversation turns
  // from a capped lines() signal whenever new CLI output arrives. With
  // default CD, every parent change-detection pass also walked through
  // the full template (markdown blocks, tool chips, scroll anchor) -
  // measurable lag during a busy run with hundreds of log lines.
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="activity-log" [class.activity-log--embedded]="variant() === 'embedded'">
      <div class="activity-log__toolbar">
        <div class="activity-log__tabs" role="tablist">
          <button class="activity-log__tab"
                  data-testid="activity-log-mode-conversation"
                  [class.activity-log__tab--active]="mode() === 'conversation'"
                  (click)="mode.set('conversation')">
            Conversation
          </button>
          <button class="activity-log__tab"
                  data-testid="activity-log-mode-trace"
                  [class.activity-log__tab--active]="mode() === 'trace'"
                  (click)="mode.set('trace')">
            Trace
          </button>
        </div>
        <div class="activity-log__actions">
          @if (mode() === 'conversation') {
            <label class="activity-log__toggle" data-testid="activity-log-show-tools">
              <input type="checkbox"
                     [checked]="showTools()"
                     (change)="showTools.set(!showTools())" />
              <span>Show tool activity</span>
            </label>
          } @else {
            <label class="activity-log__toggle" data-testid="activity-log-show-debug">
              <input type="checkbox"
                     [checked]="showDebug()"
                     (change)="showDebug.set(!showDebug())" />
              <span>Show debug noise</span>
            </label>
          }
          <button class="activity-log__btn"
                  data-testid="activity-log-copy"
                  [title]="copyTooltip()"
                  [disabled]="copyDisabled()"
                  (click)="copyVisible()">{{ copyLabel() }}</button>
        </div>
      </div>

      <div #body
           class="activity-log__body"
           [style.max-height]="bodyMaxHeight()"
           data-testid="activity-log-body"
           (scroll)="onBodyScroll()">
        @if (!stickToBottom()) {
          <button type="button"
                  class="activity-log__jump"
                  data-testid="activity-log-jump-bottom"
                  (click)="jumpToBottom()">↓ Jump to latest</button>
        }

        @if (mode() === 'conversation') {
          <div class="convo" data-testid="activity-log-conversation">
            @for (item of visibleConversation(); track item.turn.id) {
              <article class="convo-turn"
                       [class.convo-turn--user]="item.turn.kind === 'user'"
                       [class.convo-turn--agent]="item.turn.kind === 'agent'"
                       [class.convo-turn--tools]="item.turn.kind === 'tools'"
                       [class.convo-turn--system]="item.turn.kind === 'system'"
                       [class.convo-turn--orchestrator]="item.turn.kind === 'orchestrator'"
                       [class.convo-turn--error]="item.turn.status === 'error'"
                       [attr.data-testid]="testIdFor(item.turn)">
                @switch (item.turn.kind) {
                  @case ('user') {
                    <header class="convo-turn__head">
                      <span class="convo-turn__role">You</span>
                      <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </header>
                    <div class="convo-turn__body convo-turn__body--user"
                         [innerHTML]="item.bodyHtml"></div>
                  }
                  @case ('agent') {
                    <header class="convo-turn__head">
                      <span class="convo-turn__role">Agent</span>
                      <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </header>
                    <div class="convo-turn__body convo-turn__body--agent markdown"
                         [innerHTML]="item.bodyHtml"></div>
                  }
                  @case ('system') {
                    <header class="convo-turn__head">
                      <span class="convo-turn__role">System</span>
                      <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </header>
                    <div class="convo-turn__body convo-turn__body--system"
                         [innerHTML]="item.bodyHtml"></div>
                  }
                  @case ('orchestrator') {
                    @if (item.steer; as s) {
                      <article class="steer-card"
                               data-testid="orchestrator-steer-card">
                        <header class="steer-card__head">
                          <span class="steer-card__icon" aria-hidden="true">?</span>
                          <span class="steer-card__role">Orchestrator needs your input</span>
                          <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                        </header>
                        <dl class="steer-card__fields">
                          <dt>Need</dt>
                          <dd data-testid="orchestrator-steer-need">{{ s.need }}</dd>
                          @if (s.why) {
                            <dt>Why</dt>
                            <dd data-testid="orchestrator-steer-why">{{ s.why }}</dd>
                          }
                        </dl>
                        @if (s.options.length > 0) {
                          <div class="steer-card__options"
                               role="group"
                               aria-label="Suggested replies"
                               data-testid="orchestrator-steer-options">
                            @for (opt of s.options; track $index; let idx = $index) {
                              <button type="button"
                                      class="steer-card__option"
                                      [attr.data-testid]="'orchestrator-steer-option-' + idx"
                                      (click)="onSteerOptionClick(opt, idx)">
                                <span class="steer-card__option-label">{{ steerOptionLabel(idx) }}</span>
                                <span class="steer-card__option-text">{{ opt }}</span>
                              </button>
                            }
                          </div>
                        }
                        @if (s.needsScreenshot) {
                          <div class="steer-card__upload">
                            <button type="button"
                                    class="steer-card__upload-btn"
                                    data-testid="orchestrator-steer-upload"
                                    (click)="onSteerUploadClick()">
                              📎 Send screenshot
                            </button>
                          </div>
                        }
                      </article>
                    } @else {
                      <header class="convo-turn__head">
                        <span class="convo-turn__role">⚙ Orchestrator</span>
                        <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                      </header>
                      <div class="convo-turn__body convo-turn__body--orchestrator"
                           [innerHTML]="item.bodyHtml"></div>
                    }
                  }
                  @case ('tools') {
                    <button type="button"
                            class="convo-tools"
                            (click)="toggleTurn(item.turn.id)"
                            [attr.aria-expanded]="isTurnExpanded(item.turn)"
                            data-testid="convo-tools-pill">
                      <span class="convo-tools__chevron">{{ isTurnExpanded(item.turn) ? 'v' : '>' }}</span>
                      <span class="convo-tools__icon" aria-hidden="true">⚙</span>
                      <span class="convo-tools__chips">
                        @for (chip of item.toolChips; track chip.kind) {
                          <span class="convo-tools__chip"
                                [attr.data-kind]="chip.kind"
                                [attr.data-testid]="'convo-tools-chip-' + chip.kind">
                            <span class="convo-tools__chip-label">{{ chip.label }}</span>
                            <span class="convo-tools__chip-count">×{{ chip.count }}</span>
                          </span>
                        }
                      </span>
                      @if (item.toolDuration) {
                        <span class="convo-tools__duration"
                              data-testid="convo-tools-duration"
                              [title]="'Tool activity took ' + item.toolDuration">{{ item.toolDuration }}</span>
                      }
                      <span class="convo-tools__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </button>
                    @if (isTurnExpanded(item.turn)) {
                      <div class="convo-tools__detail">
                        @for (bin of item.toolBins; track bin.kind) {
                          <div class="convo-tools__bin">
                            <div class="convo-tools__bin-head">
                              <span class="convo-tools__kind">{{ kindLabel(bin.kind) }}</span>
                              <span class="convo-tools__bin-count">×{{ bin.count }}</span>
                            </div>
                            @for (group of bin.groups; track group.id) {
                              <div class="convo-tools__group">
                                <span class="convo-tools__title">{{ group.title }}</span>
                                @if (group.subtitle) {
                                  <span class="convo-tools__sub">{{ group.subtitle }}</span>
                                }
                              </div>
                            }
                          </div>
                        }
                      </div>
                    }
                  }
                }
              </article>
            }
            @if (visibleConversation().length === 0) {
              <div class="activity-log__empty">
                {{ lines().length === 0 ? 'No activity output yet.' : 'Conversation is empty - try switching to Trace.' }}
              </div>
            }
          </div>
        } @else {
          <div class="trace" data-testid="activity-log-trace">
            @for (group of visibleTraceGroups(); track group.id) {
              <article class="trace-group"
                       [class.trace-group--error]="group.status === 'error'"
                       [class.trace-group--user]="group.lines[0]?.stream === 'user'"
                       [attr.data-testid]="group.lines[0]?.stream === 'user' ? 'trace-group-user' : null">
                <button class="trace-group__header" (click)="toggleGroup(group)">
                  <span class="trace-group__chevron">{{ isExpanded(group) ? 'v' : '>' }}</span>
                  <span class="trace-group__kind">{{ kindLabel(group.kind) }}</span>
                  <span class="trace-group__title">{{ group.title }}</span>
                  <span class="trace-group__count">{{ group.lines.length }}</span>
                </button>
                @if (group.subtitle) {
                  <div class="trace-group__subtitle">{{ group.subtitle }}</div>
                }
                @if (isExpanded(group)) {
                  <div class="trace-group__lines">
                    @for (line of group.lines; track $index) {
                      <div class="trace-line"
                           [class.trace-line--stderr]="line.stream === 'stderr'"
                           [class.trace-line--user]="line.stream === 'user'">
                        <span class="trace-line__time">{{ formatTime(line.timestamp) }}</span>
                        <span class="trace-line__stream">{{ streamLabel(line.stream) }}</span>
                        <span class="trace-line__text">{{ line.text }}</span>
                      </div>
                    }
                  </div>
                }
              </article>
            }
            @if (visibleTraceGroups().length === 0) {
              <div class="activity-log__empty">
                {{ lines().length === 0 ? 'No activity output yet.' : 'Nothing matches - try toggling Show debug noise.' }}
              </div>
            }
          </div>
        }

        @if (liveStatus(); as ls) {
          <div class="live-status"
               [attr.data-kind]="ls.kind"
               data-testid="activity-log-live-status"
               role="status"
               aria-live="polite">
            <span class="live-status__pulse" aria-hidden="true">
              <span></span><span></span><span></span>
            </span>
            <span class="live-status__verb" data-testid="activity-log-live-verb">{{ ls.verb }}</span>
            @if (ls.detail) {
              <span class="live-status__detail" data-testid="activity-log-live-detail">{{ ls.detail }}</span>
            }
            @if (formatSince(ls.sinceMs); as since) {
              <span class="live-status__since"
                    data-testid="activity-log-live-since"
                    [title]="'Time since the last activity-log line'">{{ since }}</span>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      min-height: 0;
      flex: 1;
    }
    .activity-log {
      display: flex;
      flex-direction: column;
      min-height: 0;
      flex: 1;
      background: #0d0d1a;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      overflow: hidden;
    }
    .activity-log--embedded {
      background: transparent;
      border: 0;
      border-radius: 0;
    }
    .activity-log__toolbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      padding: 8px 12px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      background: rgba(255,255,255,0.03);
      flex-wrap: wrap;
    }
    .activity-log--embedded .activity-log__toolbar {
      padding: 0 0 8px;
      background: transparent;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .activity-log__tabs,
    .activity-log__actions {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }
    .activity-log__tab,
    .activity-log__btn {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      border-radius: 6px;
      padding: 5px 12px;
      font-size: 12.5px;
      cursor: pointer;
      transition: background-color 80ms ease, color 80ms ease, border-color 80ms ease;
    }
    .activity-log__tab:hover,
    .activity-log__btn:hover:not(:disabled) {
      color: #e2e8f0;
      border-color: rgba(255,255,255,0.16);
    }
    .activity-log__tab--active {
      color: #ddd6fe;
      border-color: rgba(196,181,253,0.5);
      background: rgba(124,58,237,0.22);
    }
    .activity-log__btn:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
    .activity-log__toggle {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: #cbd5e1;
      font-size: 12px;
      white-space: nowrap;
      user-select: none;
    }
    .activity-log__toggle input { accent-color: #7c3aed; }

    .activity-log__body {
      flex: 1 1 auto;
      overflow-y: auto;
      padding: 12px 14px;
      min-height: 160px;
      font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif;
      font-size: 13.5px;
      line-height: 1.55;
      position: relative;
      scroll-behavior: smooth;
    }
    .activity-log--embedded .activity-log__body {
      padding: 8px 0 0;
      min-height: 132px;
    }
    .activity-log__jump {
      position: sticky;
      top: 0;
      float: right;
      margin: 0 0 6px 8px;
      padding: 4px 10px;
      font-size: 11px;
      color: #c4b5fd;
      background: rgba(76,29,149,0.65);
      border: 1px solid rgba(196,181,253,0.4);
      border-radius: 999px;
      cursor: pointer;
      z-index: 2;
      box-shadow: 0 2px 6px rgba(0,0,0,0.4);
    }
    .activity-log__empty {
      padding: 28px 12px;
      color: #64748b;
      text-align: center;
    }

    /* ===== Conversation mode ===== */
    .convo {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }
    .convo-turn {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 10px 14px 12px;
      border-radius: 12px;
      border: 1px solid rgba(148,163,184,0.16);
      background: rgba(15,23,42,0.55);
    }
    .convo-turn--agent {
      background: rgba(76,29,149,0.16);
      border-color: rgba(196,181,253,0.28);
    }
    .convo-turn--user {
      background: rgba(13,148,136,0.16);
      border-color: rgba(94,234,212,0.32);
    }
    .convo-turn--system {
      background: rgba(127,29,29,0.18);
      border-color: rgba(251,113,133,0.4);
    }
    .convo-turn--orchestrator {
      background: rgba(120,113,108,0.18);
      border-color: rgba(217,119,6,0.45);
    }

    /* Steer card: distinct visual treatment so the user immediately sees
       the orchestrator handed back a concrete unblocking ask, not a generic
       decision line. Question-mark glyph plus a left-edge accent border. */
    .steer-card {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px 14px 14px 16px;
      border-radius: 12px;
      border: 1px solid rgba(217,119,6,0.55);
      border-left: 4px solid #f59e0b;
      background: linear-gradient(180deg, rgba(120,53,15,0.22), rgba(120,53,15,0.10));
      box-shadow: 0 0 0 1px rgba(245,158,11,0.10) inset;
    }
    .steer-card__head {
      display: flex;
      align-items: center;
      gap: 10px;
      font-size: 11.5px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      font-weight: 700;
      color: #fde68a;
    }
    .steer-card__icon {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 20px;
      height: 20px;
      border-radius: 999px;
      background: #f59e0b;
      color: #1c1917;
      font-weight: 800;
      font-size: 13px;
      line-height: 1;
    }
    .steer-card__role { letter-spacing: 0.05em; }
    .steer-card__fields {
      margin: 0;
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      font-size: 13.5px;
      color: #fef3c7;
    }
    .steer-card__fields dt {
      font-size: 11px;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      font-weight: 700;
      color: #fbbf24;
      align-self: baseline;
      padding-top: 2px;
    }
    .steer-card__fields dd {
      margin: 0;
      color: #fef3c7;
      line-height: 1.5;
    }
    .steer-card__options {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding-top: 4px;
    }
    .steer-card__option {
      display: flex;
      align-items: baseline;
      gap: 10px;
      padding: 8px 12px;
      background: rgba(15,23,42,0.45);
      border: 1px solid rgba(245,158,11,0.35);
      border-radius: 8px;
      color: #fef3c7;
      font: inherit;
      font-size: 13px;
      text-align: left;
      cursor: pointer;
      transition: background-color 80ms ease, border-color 80ms ease;
    }
    .steer-card__option:hover,
    .steer-card__option:focus-visible {
      background: rgba(245,158,11,0.15);
      border-color: rgba(245,158,11,0.6);
      outline: none;
    }
    .steer-card__option-label {
      flex: 0 0 auto;
      width: 22px;
      height: 22px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border-radius: 999px;
      background: rgba(245,158,11,0.25);
      color: #fde68a;
      font-weight: 700;
      font-size: 11.5px;
    }
    .steer-card__option-text { flex: 1 1 auto; }
    .steer-card__upload {
      display: flex;
      justify-content: flex-start;
      padding-top: 2px;
    }
    .steer-card__upload-btn {
      padding: 6px 12px;
      background: rgba(15,23,42,0.45);
      border: 1px dashed rgba(245,158,11,0.45);
      border-radius: 8px;
      color: #fde68a;
      font: inherit;
      font-size: 12.5px;
      cursor: pointer;
    }
    .steer-card__upload-btn:hover,
    .steer-card__upload-btn:focus-visible {
      background: rgba(245,158,11,0.18);
      border-style: solid;
      outline: none;
    }
    .convo-turn--tools {
      padding: 0;
      background: transparent;
      border: 0;
    }
    .convo-turn__head {
      display: flex;
      align-items: baseline;
      gap: 8px;
      font-size: 11px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      font-weight: 700;
      color: #94a3b8;
    }
    .convo-turn--agent .convo-turn__head { color: #ddd6fe; }
    .convo-turn--user .convo-turn__head { color: #99f6e4; }
    .convo-turn--system .convo-turn__head { color: #fecaca; }
    .convo-turn--orchestrator .convo-turn__head { color: #fbbf24; }
    .convo-turn__time {
      margin-left: auto;
      font-weight: 500;
      color: #64748b;
      letter-spacing: 0;
      text-transform: none;
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    .convo-turn__body {
      color: #e2e8f0;
      font-size: 13.5px;
      line-height: 1.6;
      word-break: break-word;
    }
    .convo-turn__body--user { color: #ccfbf1; white-space: pre-wrap; }
    .convo-turn__body--system { color: #fecaca; white-space: pre-wrap; }
    .convo-turn__body--agent { color: #ede9fe; }

    /* Markdown rendering inside the agent bubble.
       Tuned for read-the-result density: agent reports often consist of bullet
       lists punctuated by lone-bold-line "section headers" (e.g. **Fixes**,
       **Evidence**) rather than real h1/h2/h3. The line-height and list
       spacing below make those breathe, and the :has() selector below treats
       a paragraph that is just one <strong> as the section break it actually
       is, with a real top margin. */
    .markdown { line-height: 1.55; }
    .markdown :first-child { margin-top: 0; }
    .markdown :last-child  { margin-bottom: 0; }
    .markdown p {
      margin: 0.5em 0 0.8em;
    }
    .markdown h1, .markdown h2, .markdown h3 {
      margin: 1.2em 0 0.4em;
      color: #f5f3ff;
      font-weight: 700;
      line-height: 1.3;
    }
    .markdown h1 { font-size: 1.25em; }
    .markdown h2 { font-size: 1.15em; }
    .markdown h3 { font-size: 1.05em; }
    .markdown ul, .markdown ol {
      margin: 0.5em 0 0.9em;
      padding-left: 1.4em;
    }
    .markdown li { margin: 0.4em 0; }
    .markdown li > p { margin: 0.25em 0; }
    .markdown li > ul, .markdown li > ol { margin: 0.3em 0; }
    .markdown strong { color: #fafaff; font-weight: 700; }
    .markdown em { color: #ddd6fe; }
    /* Lone-bold paragraph = section header in agent prose. Modern browsers
       all ship :has() now; the fallback is just no extra spacing, which is
       the previous behaviour. */
    .markdown p:has(> strong:only-child) {
      margin-top: 1.3em;
      margin-bottom: 0.4em;
      color: #f5f3ff;
      font-size: 1.02em;
    }
    .markdown p:has(> strong:only-child):first-child { margin-top: 0; }
    .markdown a {
      color: #a5b4fc;
      text-decoration: underline;
      text-underline-offset: 2px;
    }
    .markdown a:hover { color: #c7d2fe; }
    .markdown code {
      background: rgba(2,6,23,0.6);
      border: 1px solid rgba(148,163,184,0.18);
      border-radius: 4px;
      padding: 1px 5px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 0.92em;
      color: #fef3c7;
    }
    .markdown pre {
      margin: 0.6em 0;
      padding: 10px 12px;
      background: rgba(2,6,23,0.7);
      border: 1px solid rgba(148,163,184,0.16);
      border-radius: 8px;
      overflow: auto;
      max-height: 360px;
    }
    .markdown pre code {
      background: transparent;
      border: 0;
      padding: 0;
      font-size: 12px;
      color: #e0f2fe;
      white-space: pre;
    }

    /* Tool burst pill — intentionally muted: tool activity is supporting
       evidence, the agent / user bubbles carry the conversation. Chips show
       per-kind weight so a long run of "Read, Read, Read..." lands as a
       single compact badge ("Read ×12") instead of stealing focus. */
    .convo-tools {
      width: 100%;
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 4px 10px;
      background: transparent;
      border: 0;
      border-top: 1px solid rgba(148,163,184,0.08);
      border-bottom: 1px solid rgba(148,163,184,0.08);
      border-radius: 0;
      color: #64748b;
      font: inherit;
      font-size: 11.5px;
      cursor: pointer;
      text-align: left;
      opacity: 0.75;
      transition: opacity 80ms ease, color 80ms ease, background-color 80ms ease;
    }
    .convo-tools:hover,
    .convo-tools[aria-expanded='true'] {
      opacity: 1;
      color: #cbd5e1;
      background: rgba(30,41,59,0.35);
    }
    .convo-tools__chevron { color: #475569; font-size: 10px; flex: 0 0 auto; }
    .convo-tools__icon { color: #475569; flex: 0 0 auto; font-size: 11px; }
    .convo-tools__chips {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 6px;
      flex: 1 1 auto;
      min-width: 0;
    }
    .convo-tools__chip {
      display: inline-flex;
      align-items: baseline;
      gap: 3px;
      padding: 1px 7px;
      border-radius: 999px;
      background: rgba(148,163,184,0.10);
      border: 1px solid rgba(148,163,184,0.16);
      font-size: 10.5px;
      line-height: 1.5;
    }
    .convo-tools__chip[data-kind='read']    { color: #7dd3fc; border-color: rgba(125,211,252,0.28); background: rgba(125,211,252,0.08); }
    .convo-tools__chip[data-kind='search']  { color: #c4b5fd; border-color: rgba(196,181,253,0.28); background: rgba(196,181,253,0.08); }
    .convo-tools__chip[data-kind='command'] { color: #fcd34d; border-color: rgba(252,211,77,0.28); background: rgba(252,211,77,0.08); }
    .convo-tools__chip[data-kind='edit']    { color: #6ee7b7; border-color: rgba(110,231,183,0.28); background: rgba(110,231,183,0.08); }
    .convo-tools__chip[data-kind='task']    { color: #f9a8d4; border-color: rgba(249,168,212,0.28); background: rgba(249,168,212,0.08); }
    .convo-tools__chip[data-kind='todo']    { color: #fdba74; border-color: rgba(253,186,116,0.28); background: rgba(253,186,116,0.08); }
    .convo-tools__chip-label {
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    .convo-tools__chip-count {
      font-variant-numeric: tabular-nums;
      opacity: 0.85;
      font-size: 10px;
    }
    .convo-tools__duration {
      flex: 0 0 auto;
      color: #475569;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
      padding: 1px 6px;
      border-radius: 999px;
      background: rgba(148,163,184,0.06);
    }
    .convo-tools__time {
      flex: 0 0 auto;
      color: #475569;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }

    .convo-tools__detail {
      margin: 4px 0 0 16px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 8px 10px;
      background: rgba(2,6,23,0.45);
      border: 1px solid rgba(148,163,184,0.12);
      border-radius: 8px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11.5px;
      color: #cbd5e1;
    }
    .convo-tools__bin {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .convo-tools__bin-head {
      display: flex;
      align-items: baseline;
      gap: 6px;
      padding-bottom: 2px;
      border-bottom: 1px solid rgba(148,163,184,0.10);
      margin-bottom: 2px;
    }
    .convo-tools__bin-count {
      color: #94a3b8;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }
    .convo-tools__group {
      display: grid;
      grid-template-columns: minmax(0, auto) minmax(0, 1fr);
      gap: 8px;
      align-items: baseline;
      padding-left: 8px;
    }
    .convo-tools__kind {
      color: #7dd3fc;
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-weight: 700;
    }
    .convo-tools__title {
      color: #e2e8f0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .convo-tools__sub {
      color: #94a3b8;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    /* ===== Trace mode ===== */
    .trace {
      display: flex;
      flex-direction: column;
      gap: 6px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
    }
    .trace-group {
      border: 1px solid rgba(148,163,184,0.16);
      border-left: 3px solid #38bdf8;
      border-radius: 8px;
      background: rgba(15,23,42,0.5);
      overflow: hidden;
    }
    .trace-group--error {
      border-left-color: #fb7185;
      background: rgba(127,29,29,0.18);
    }
    .trace-group--user {
      border-left-color: #14b8a6;
      background: rgba(13,148,136,0.12);
    }
    .trace-group__header {
      width: 100%;
      display: grid;
      grid-template-columns: 14px minmax(86px, auto) minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      padding: 6px 10px;
      color: #e2e8f0;
      background: transparent;
      border: 0;
      cursor: pointer;
      font: inherit;
      text-align: left;
    }
    .trace-group__chevron, .trace-group__count {
      color: #94a3b8;
      font-variant-numeric: tabular-nums;
    }
    .trace-group__kind {
      color: #a7f3d0;
      font-size: 10px;
      text-transform: uppercase;
      font-weight: 700;
      letter-spacing: 0.04em;
    }
    .trace-group__title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .trace-group__subtitle {
      padding: 0 10px 6px 110px;
      color: #94a3b8;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .trace-group__lines {
      border-top: 1px solid rgba(255,255,255,0.05);
      padding: 4px 0;
    }
    .trace-line {
      display: grid;
      grid-template-columns: 72px 34px minmax(0, 1fr);
      gap: 8px;
      padding: 2px 10px;
      align-items: baseline;
    }
    .trace-line:hover { background: rgba(255,255,255,0.04); }
    .trace-line__time {
      color: #64748b;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }
    .trace-line__stream {
      color: #4ade80;
      background: rgba(34,197,94,0.1);
      border-radius: 3px;
      text-align: center;
      font-size: 9px;
      font-weight: 700;
      padding: 1px 4px;
    }
    .trace-line--stderr .trace-line__stream {
      color: #fb7185;
      background: rgba(251,113,133,0.1);
    }
    .trace-line--stderr .trace-line__text { color: #fca5a5; }
    .trace-line--user .trace-line__stream {
      color: #5eead4;
      background: rgba(13,148,136,0.18);
    }
    .trace-line--user .trace-line__text { color: #ccfbf1; }
    .trace-line__text {
      color: #e2e8f0;
      white-space: pre-wrap;
      word-break: break-word;
    }

    /* ===== Live status (the "agent is alive" row) ===== */
    /* The row sits at the bottom of the body in both Conversation and
       Trace modes. It is intentionally subtle - the conversation /
       trace content carries the message, the live row only signals
       "still going". Three pulsing dots + a verb + an optional target
       + an optional "since last line" chip. */
    .live-status {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 12px;
      padding: 8px 12px;
      border-radius: 999px;
      background: rgba(124,58,237,0.10);
      border: 1px solid rgba(196,181,253,0.22);
      color: #ddd6fe;
      font-size: 12.5px;
      line-height: 1.4;
      align-self: flex-start;
      max-width: 100%;
      animation: live-status-fade-in 220ms ease-out both;
    }
    @keyframes live-status-fade-in {
      from { opacity: 0; transform: translateY(4px); }
      to   { opacity: 1; transform: translateY(0); }
    }
    .live-status[data-kind='tool']        { border-color: rgba(125,211,252,0.32); background: rgba(56,189,248,0.10); color: #bae6fd; }
    .live-status[data-kind='agent']       { border-color: rgba(196,181,253,0.32); background: rgba(124,58,237,0.12); color: #ddd6fe; }
    .live-status[data-kind='user']        { border-color: rgba(94,234,212,0.32);  background: rgba(13,148,136,0.12);  color: #ccfbf1; }
    .live-status[data-kind='orchestrator'] { border-color: rgba(252,211,77,0.36); background: rgba(217,119,6,0.10);   color: #fde68a; }
    .live-status[data-kind='recovering']  { border-color: rgba(251,113,133,0.45); background: rgba(127,29,29,0.18);   color: #fecaca; }
    .live-status[data-kind='starting']    { border-color: rgba(148,163,184,0.32); background: rgba(148,163,184,0.10); color: #cbd5e1; }

    .live-status__pulse {
      display: inline-flex;
      gap: 3px;
      align-items: center;
      flex: 0 0 auto;
    }
    .live-status__pulse span {
      width: 5px;
      height: 5px;
      border-radius: 999px;
      background: currentColor;
      opacity: 0.55;
      animation: live-pulse 1.2s ease-in-out infinite;
    }
    .live-status__pulse span:nth-child(2) { animation-delay: 0.18s; }
    .live-status__pulse span:nth-child(3) { animation-delay: 0.36s; }
    @keyframes live-pulse {
      0%, 80%, 100% { opacity: 0.35; transform: translateY(0); }
      40%           { opacity: 1;    transform: translateY(-2px); }
    }

    .live-status__verb {
      font-weight: 700;
      letter-spacing: 0.01em;
      flex: 0 0 auto;
    }
    .live-status__detail {
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
      color: inherit;
      opacity: 0.95;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      min-width: 0;
      flex: 0 1 auto;
    }
    .live-status__since {
      margin-left: auto;
      flex: 0 0 auto;
      padding: 1px 7px;
      border-radius: 999px;
      background: rgba(15,23,42,0.45);
      color: rgba(255,255,255,0.7);
      font-size: 10.5px;
      font-variant-numeric: tabular-nums;
      letter-spacing: 0.02em;
    }

    @media (max-width: 720px) {
      .activity-log__toolbar {
        align-items: flex-start;
        flex-direction: column;
      }
      .trace-group__header { grid-template-columns: 14px minmax(0, 1fr); }
      .trace-line { grid-template-columns: 1fr; }
    }
  `]
})
export class ActivityLogViewComponent implements AfterViewInit, OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly bodyMaxHeight = input('400px');
  readonly variant = input<'framed' | 'embedded'>('framed');
  /**
   * When true the live-status row renders at the bottom of the body
   * (in both Conversation and Trace mode). The row pulses, names what
   * the agent is doing right now, and counts seconds since the last
   * line so the user always sees that the run is alive.
   */
  readonly isRunning = input(false);

  /**
   * Emitted when the user picks a suggested reply from a steer card. The
   * parent (typically the protocol pane) is expected to pre-fill its
   * compose box with the option text so the user can edit and send.
   */
  readonly applyComposeSuggestion = output<string>();
  /**
   * Emitted when the user clicks "Send screenshot" on a steer card whose
   * Need line mentions a screenshot. The parent owns the attachment
   * uploader (it knows the job id) and opens it in response.
   */
  readonly requestUploadScreenshot = output<void>();

  readonly mode = signal<ViewMode>('conversation');
  readonly showTools = signal(false);
  readonly showDebug = signal(false);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  /** Open/closed state for tool bursts (Conversation) and groups (Trace), keyed by id. */
  readonly expandedTurns = signal<Record<string, boolean>>({});
  readonly expandedGroups = signal<Record<string, boolean>>({});
  readonly stickToBottom = signal(true);

  readonly parsedGroups = computed(() => parseActivityLog(this.lines()));

  readonly conversationTurns = computed<ConversationTurn[]>(
    () => buildConversationTurns(this.parsedGroups())
  );

  /**
   * Conversation feed minus tool bursts when "Show tool activity" is off.
   * Bursts are kept in the data so the toggle is reversible without re-parsing.
   */
  readonly visibleConversation = computed<RenderedTurn[]>(() => {
    const turns = this.conversationTurns();
    const showTools = this.showTools();
    return turns
      .filter((turn) => showTools || turn.kind !== 'tools')
      .map((turn) => this.renderTurn(turn));
  });

  /**
   * Trace feed: every parsed group, optionally filtered for "debug noise" -
   * which currently means session-init markers and groups whose only line is
   * a blank or whitespace-only string. The bar to add a per-kind filter is
   * the same friction the redesign was meant to remove, so we don't.
   */
  readonly visibleTraceGroups = computed<ActivityLogGroup[]>(() => {
    const groups = this.parsedGroups();
    if (this.showDebug()) return groups;
    return groups.filter((group) => !isDebugNoise(group));
  });

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;
  private readonly sanitizer = inject(DomSanitizer);

  /**
   * 1 s wall-clock ticker that drives the "since last line" counter on
   * the live-status row. Only ticks while {@link isRunning} is true so
   * idle detail panels do not pay for a setInterval. NowTickService
   * exists but it ticks at 15 s, which is far too coarse for the
   * "agent is alive" feel the user asked for.
   */
  private readonly nowMs = signal(Date.now());
  private liveTicker: ReturnType<typeof setInterval> | null = null;
  private readonly liveTickerEffect = effect(() => {
    if (this.isRunning()) {
      if (!this.liveTicker) {
        this.nowMs.set(Date.now());
        this.liveTicker = setInterval(() => this.nowMs.set(Date.now()), 1000);
      }
    } else if (this.liveTicker) {
      clearInterval(this.liveTicker);
      this.liveTicker = null;
    }
  });

  readonly liveStatus = computed<LiveStatus | null>(() =>
    deriveLiveStatus(this.lines(), this.isRunning(), this.nowMs())
  );

  formatSince(ms: number): string {
    return formatLiveSince(ms);
  }

  private readonly autoScrollEffect = effect(() => {
    this.lines();
    this.mode();
    this.visibleConversation();
    this.visibleTraceGroups();
    this.expandedTurns();
    this.expandedGroups();
    if (!this.stickToBottom()) return;
    this.scheduleScrollToBottom();
  });

  ngAfterViewInit(): void {
    this.scheduleScrollToBottom();
  }

  ngOnDestroy(): void {
    if (this.scrollFrame !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.scrollFrame);
    }
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
    if (this.liveTicker !== null) {
      clearInterval(this.liveTicker);
      this.liveTicker = null;
    }
    this.autoScrollEffect.destroy();
    this.liveTickerEffect.destroy();
  }

  testIdFor(turn: ConversationTurn): string | null {
    if (turn.kind === 'user') return 'convo-turn-user';
    if (turn.kind === 'agent') return 'convo-turn-agent';
    if (turn.kind === 'tools') return 'convo-turn-tools';
    if (turn.kind === 'system') return 'convo-turn-system';
    if (turn.kind === 'orchestrator') return 'convo-turn-orchestrator';
    return null;
  }

  copyDisabled(): boolean {
    if (this.mode() === 'conversation') return this.visibleConversation().length === 0;
    return this.visibleTraceGroups().length === 0;
  }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  copyTooltip(): string {
    return this.mode() === 'conversation'
      ? 'Copy the visible conversation transcript'
      : 'Copy the visible trace';
  }

  async copyVisible(): Promise<void> {
    const text = this.buildCopyText();
    if (!text) return;
    const ok = await copyTextToClipboard(text);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }

  private buildCopyText(): string {
    if (this.mode() === 'conversation') {
      const parts: string[] = [];
      for (const item of this.visibleConversation()) {
        const head = `[${this.formatTime(item.turn.timestamp)}] ${roleHeading(item.turn.kind)}`;
        if (item.turn.kind === 'tools') {
          const chipText = item.toolChips.map((c) => `${c.label} ×${c.count}`).join(', ');
          const dur = item.toolDuration ? ` (${item.toolDuration})` : '';
          parts.push(`${head} ${chipText}${dur}`);
        } else {
          parts.push(`${head}\n${item.turn.text}`);
        }
      }
      return parts.join('\n\n');
    }
    const parts: string[] = [];
    for (const group of this.visibleTraceGroups()) {
      parts.push(`=== ${activityKindLabel(group.kind)} — ${group.title} ===`);
      if (group.subtitle) parts.push(group.subtitle);
      for (const line of group.lines) {
        parts.push(`[${this.formatTime(line.timestamp)}] ${this.streamLabel(line.stream)} ${line.text}`);
      }
      parts.push('');
    }
    return parts.join('\n').trimEnd();
  }

  onBodyScroll(): void {
    if (this.suppressScrollEvent) return;
    const el = this.bodyRef()?.nativeElement;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    this.stickToBottom.set(distanceFromBottom <= 24);
  }

  jumpToBottom(): void {
    this.stickToBottom.set(true);
    this.scheduleScrollToBottom();
  }

  private scheduleScrollToBottom(): void {
    if (typeof requestAnimationFrame === 'undefined') return;
    if (this.scrollFrame !== null) cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = null;
      const el = this.bodyRef()?.nativeElement;
      if (!el) return;
      this.suppressScrollEvent = true;
      el.scrollTop = el.scrollHeight;
      requestAnimationFrame(() => { this.suppressScrollEvent = false; });
    });
  }

  kindLabel(kind: ActivityLogKind): string {
    return activityKindLabel(kind);
  }

  streamLabel(stream: string): string {
    if (stream === 'stderr') return 'ERR';
    if (stream === 'user') return 'YOU';
    if (stream === 'system') return 'SYS';
    return 'OUT';
  }

  isExpanded(group: ActivityLogGroup): boolean {
    return this.expandedGroups()[group.id] ?? !group.collapsedByDefault;
  }

  toggleGroup(group: ActivityLogGroup): void {
    const next = !this.isExpanded(group);
    this.expandedGroups.update((m) => ({ ...m, [group.id]: next }));
  }

  isTurnExpanded(turn: ConversationTurn): boolean {
    return this.expandedTurns()[turn.id] ?? false;
  }

  toggleTurn(id: string): void {
    this.expandedTurns.update((m) => ({ ...m, [id]: !m[id] }));
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    });
  }

  private renderTurn(turn: ConversationTurn): RenderedTurn {
    if (turn.kind === 'tools') {
      return {
        turn,
        bodyHtml: null,
        toolChips: buildToolChips(turn),
        toolDuration: formatBurstDuration(turn.toolSummary?.durationMs ?? 0),
        toolBins: binToolBurstByKind(turn.groups)
      };
    }
    if (turn.kind === 'orchestrator') {
      const firstLine = turn.groups[0]?.lines[0];
      const steer = firstLine ? parseOrchestratorSteer(firstLine.text) : null;
      if (steer) {
        return {
          turn,
          bodyHtml: null,
          toolChips: [],
          toolDuration: '',
          toolBins: [],
          steer
        };
      }
    }
    const html = turn.kind === 'agent'
      ? this.sanitizer.bypassSecurityTrustHtml(markdownToHtml(turn.text))
      // For user/system we keep plain text but still need SafeHtml in the
      // template path. We escape ourselves and bypassSecurityTrustHtml so the
      // template binding doesn't double-escape.
      : this.sanitizer.bypassSecurityTrustHtml(escapeForPlain(turn.text));
    return { turn, bodyHtml: html, toolChips: [], toolDuration: '', toolBins: [] };
  }

  /**
   * One-letter label for a steer option ("A", "B", "C", ...). Mirrors the
   * grammar the orchestrator emits, so the chat row reads naturally
   * regardless of which marker style the model chose (`A)`, `1)`, `-`).
   */
  steerOptionLabel(index: number): string {
    if (index < 0) return '';
    if (index < 26) return String.fromCharCode('A'.charCodeAt(0) + index);
    return `${index + 1}`;
  }

  onSteerOptionClick(option: string, _index: number): void {
    if (!option) return;
    this.applyComposeSuggestion.emit(option);
  }

  onSteerUploadClick(): void {
    this.requestUploadScreenshot.emit();
  }
}

/**
 * Treats blank/separator lines and lone session-init frames as "debug noise"
 * so Trace mode is readable without checkboxes. Anything substantive (real
 * tool calls, agent text, errors, user messages) survives.
 */
function isDebugNoise(group: ActivityLogGroup): boolean {
  if (!group.lines.length) return true;
  const allBlank = group.lines.every((l) => !l.text || l.text.trim() === '');
  if (allBlank) return true;
  if (group.kind === 'message' && /^●\s*Session\b/i.test(group.title)) return true;
  if (group.kind === 'message' && /^●\s*frame\b/i.test(group.title)) return true;
  return false;
}

/**
 * One chip per kind seen in the burst, in a deterministic order so the layout
 * doesn't shuffle as new groups stream in. Counts come straight from the
 * pre-aggregated summary (which already accounts for parser-level batches).
 */
function buildToolChips(turn: ConversationTurn): ToolChip[] {
  const summary = turn.toolSummary;
  if (!summary) return [];
  const order: ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'other'];
  const chips: ToolChip[] = [];
  for (const kind of order) {
    const count = summary.counts[kind];
    if (!count) continue;
    chips.push({ kind, label: chipKindLabel(kind), count });
  }
  return chips;
}

/** Short, capitalised kind label for the chip face ("Read", "Grep", "Edit"). */
function chipKindLabel(kind: ActivityLogKind): string {
  switch (kind) {
    case 'read': return 'Read';
    case 'search': return 'Grep';
    case 'command': return 'Run';
    case 'edit': return 'Edit';
    case 'task': return 'Task';
    case 'todo': return 'Todo';
    case 'error': return 'Error';
    case 'message': return 'Msg';
    case 'orchestrator': return 'Orch';
    case 'supervisor': return 'Sup';
    case 'other': return 'Other';
  }
}

function roleHeading(kind: ConversationTurn['kind']): string {
  switch (kind) {
    case 'agent': return 'Agent';
    case 'user': return 'You';
    case 'system': return 'System';
    case 'tools': return 'Tools';
    case 'orchestrator': return 'Orchestrator';
    case 'supervisor': return 'Supervisor';
  }
}

function escapeForPlain(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}
