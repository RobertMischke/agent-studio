import { AfterViewInit, Component, ElementRef, OnDestroy, computed, effect, input, signal, viewChild, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { CliOutputLine } from '../models/job.model';
import { copyTextToClipboard } from '../services/clipboard.util';
import {
  ActivityLogGroup,
  ActivityLogKind,
  ConversationTurn,
  activityKindLabel,
  buildConversationTurns,
  parseActivityLog
} from './activity-log.parser';
import { markdownToHtml } from './markdown-utils';

type ViewMode = 'conversation' | 'trace';

interface RenderedTurn {
  turn: ConversationTurn;
  bodyHtml: SafeHtml | null;
  /**
   * For tool bursts: a short, human label like "4 actions: 2 reads, 1 search,
   * 1 edit". Built once per turn so the template doesn't re-stringify on
   * every change-detection pass.
   */
  toolHeadline: string;
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
                    <header class="convo-turn__head">
                      <span class="convo-turn__role">⚙ Orchestrator</span>
                      <span class="convo-turn__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </header>
                    <div class="convo-turn__body convo-turn__body--orchestrator"
                         [innerHTML]="item.bodyHtml"></div>
                  }
                  @case ('tools') {
                    <button type="button"
                            class="convo-tools"
                            (click)="toggleTurn(item.turn.id)"
                            [attr.aria-expanded]="isTurnExpanded(item.turn)">
                      <span class="convo-tools__chevron">{{ isTurnExpanded(item.turn) ? 'v' : '>' }}</span>
                      <span class="convo-tools__icon" aria-hidden="true">⚙</span>
                      <span class="convo-tools__headline">{{ item.toolHeadline }}</span>
                      <span class="convo-tools__time">{{ formatTime(item.turn.timestamp) }}</span>
                    </button>
                    @if (isTurnExpanded(item.turn)) {
                      <div class="convo-tools__detail">
                        @for (group of item.turn.groups; track group.id) {
                          <div class="convo-tools__group">
                            <span class="convo-tools__kind">{{ kindLabel(group.kind) }}</span>
                            <span class="convo-tools__title">{{ group.title }}</span>
                            @if (group.subtitle) {
                              <span class="convo-tools__sub">{{ group.subtitle }}</span>
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

    /* Markdown rendering inside the agent bubble */
    .markdown :first-child { margin-top: 0; }
    .markdown :last-child  { margin-bottom: 0; }
    .markdown p {
      margin: 0 0 0.8em;
    }
    .markdown h1, .markdown h2, .markdown h3 {
      margin: 0.8em 0 0.4em;
      color: #f5f3ff;
      font-weight: 700;
      line-height: 1.3;
    }
    .markdown h1 { font-size: 1.25em; }
    .markdown h2 { font-size: 1.15em; }
    .markdown h3 { font-size: 1.05em; }
    .markdown ul, .markdown ol {
      margin: 0 0 0.8em;
      padding-left: 1.4em;
    }
    .markdown li { margin: 0.2em 0; }
    .markdown strong { color: #fafaff; font-weight: 700; }
    .markdown em { color: #ddd6fe; }
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

    /* Tool burst pill */
    .convo-tools {
      width: 100%;
      display: grid;
      grid-template-columns: 14px 18px minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      padding: 6px 12px;
      background: rgba(30,41,59,0.45);
      border: 1px dashed rgba(148,163,184,0.22);
      border-radius: 999px;
      color: #cbd5e1;
      font: inherit;
      font-size: 12px;
      cursor: pointer;
      text-align: left;
    }
    .convo-tools:hover {
      background: rgba(30,41,59,0.7);
      border-color: rgba(148,163,184,0.4);
      color: #e2e8f0;
    }
    .convo-tools__chevron { color: #64748b; font-size: 11px; }
    .convo-tools__icon { color: #7dd3fc; }
    .convo-tools__headline {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .convo-tools__time {
      color: #64748b;
      font-size: 10.5px;
      font-variant-numeric: tabular-nums;
    }
    .convo-tools__detail {
      margin: 6px 0 0 14px;
      display: flex;
      flex-direction: column;
      gap: 3px;
      padding: 8px 10px;
      background: rgba(2,6,23,0.55);
      border: 1px solid rgba(148,163,184,0.14);
      border-radius: 8px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11.5px;
      color: #cbd5e1;
    }
    .convo-tools__group {
      display: grid;
      grid-template-columns: 80px minmax(0, auto) minmax(0, 1fr);
      gap: 8px;
      align-items: baseline;
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
    this.autoScrollEffect.destroy();
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
          parts.push(`${head} ${item.toolHeadline}`);
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
        toolHeadline: buildToolHeadline(turn)
      };
    }
    const html = turn.kind === 'agent'
      ? this.sanitizer.bypassSecurityTrustHtml(markdownToHtml(turn.text))
      // For user/system we keep plain text but still need SafeHtml in the
      // template path. We escape ourselves and bypassSecurityTrustHtml so the
      // template binding doesn't double-escape.
      : this.sanitizer.bypassSecurityTrustHtml(escapeForPlain(turn.text));
    return { turn, bodyHtml: html, toolHeadline: '' };
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

function buildToolHeadline(turn: ConversationTurn): string {
  const summary = turn.toolSummary;
  if (!summary || summary.total === 0) return '0 actions';
  const parts: string[] = [];
  for (const [kind, count] of Object.entries(summary.counts)) {
    if (!count) continue;
    parts.push(`${count} ${shortKindLabel(kind as ActivityLogKind, count)}`);
  }
  const headline = `${summary.total} action${summary.total === 1 ? '' : 's'}`;
  return parts.length ? `${headline}: ${parts.join(', ')}` : headline;
}

function shortKindLabel(kind: ActivityLogKind, count: number): string {
  const plural = count !== 1;
  switch (kind) {
    case 'read': return plural ? 'reads' : 'read';
    case 'search': return plural ? 'searches' : 'search';
    case 'command': return plural ? 'commands' : 'command';
    case 'edit': return plural ? 'edits' : 'edit';
    case 'task': return plural ? 'tasks' : 'task';
    case 'todo': return plural ? 'todos' : 'todo';
    case 'error': return plural ? 'errors' : 'error';
    case 'message': return plural ? 'messages' : 'message';
    case 'orchestrator': return plural ? 'orchestrator notes' : 'orchestrator note';
    case 'other': return 'other';
  }
}

function roleHeading(kind: ConversationTurn['kind']): string {
  switch (kind) {
    case 'agent': return 'Agent';
    case 'user': return 'You';
    case 'system': return 'System';
    case 'tools': return 'Tools';
    case 'orchestrator': return 'Orchestrator';
  }
}

function escapeForPlain(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}
