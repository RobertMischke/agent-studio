import { AfterViewInit, Component, ElementRef, OnDestroy, computed, effect, input, signal, viewChild } from '@angular/core';
import { CliOutputLine } from '../models/job.model';
import { copyTextToClipboard } from '../services/clipboard.util';
import {
  ActivityLogFilters,
  ActivityLogGroup,
  ActivityLogKind,
  ChatMessage,
  activityKindLabel,
  activityLogKinds,
  buildChatMessages,
  defaultActivityLogFilters,
  filterActivityGroups,
  flattenActivityLines,
  parseActivityLog
} from './activity-log.parser';

@Component({
  selector: 'app-activity-log-view',
  standalone: true,
  template: `
    <div class="activity-log" [class.activity-log--embedded]="variant() === 'embedded'">
      <div class="activity-log__toolbar">
        <div class="activity-log__tabs" role="tablist">
          <button class="activity-log__tab"
                  data-testid="activity-log-mode-chat"
                  [class.activity-log__tab--active]="mode() === 'chat'"
                  (click)="mode.set('chat')">
            Chat
          </button>
          <button class="activity-log__tab"
                  data-testid="activity-log-mode-parsed"
                  [class.activity-log__tab--active]="mode() === 'parsed'"
                  (click)="mode.set('parsed')">
            Parsed
          </button>
          <button class="activity-log__tab"
                  data-testid="activity-log-mode-raw"
                  [class.activity-log__tab--active]="mode() === 'raw'"
                  (click)="mode.set('raw')">
            Raw
          </button>
        </div>
        <div class="activity-log__actions">
          @if (mode() !== 'chat') {
            <button class="activity-log__btn" (click)="expandAll()">Expand all</button>
            <button class="activity-log__btn" (click)="collapseAll()">Collapse all</button>
          }
          <button class="activity-log__btn"
                  data-testid="activity-log-copy"
                  [title]="copyTooltip()"
                  [disabled]="visibleLines().length === 0 && chatMessages().length === 0"
                  (click)="copyVisible()">{{ copyLabel() }}</button>
        </div>
      </div>

      <div class="activity-log__filters" aria-label="Activity log filters">
        @for (kind of filterKinds; track kind) {
          <label class="activity-log__filter">
            <input type="checkbox"
                   [checked]="filters()[kind]"
                   (change)="toggleFilter(kind)" />
            <span>{{ kindLabel(kind) }}</span>
          </label>
        }
      </div>

      <div class="activity-log__summary">
        <span>{{ visibleGroups().length }} / {{ parsedGroups().length }} groups</span>
        <span>{{ visibleLines().length }} / {{ lines().length }} lines</span>
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
        @if (mode() === 'chat') {
          <div class="activity-chat" data-testid="activity-log-chat">
            @for (msg of chatMessages(); track msg.id) {
              <article class="chat-msg"
                       [class.chat-msg--agent]="msg.role === 'agent'"
                       [class.chat-msg--tool]="msg.role === 'tool'"
                       [class.chat-msg--error]="msg.status === 'error'"
                       [attr.data-role]="msg.role">
                <div class="chat-msg__avatar" [attr.aria-hidden]="true">{{ msg.avatar }}</div>
                <div class="chat-msg__bubble">
                  <header class="chat-msg__head">
                    <span class="chat-msg__author">{{ msg.author }}</span>
                    @if (msg.kindLabel) {
                      <span class="chat-msg__pill"
                            [class.chat-msg__pill--error]="msg.status === 'error'">{{ msg.kindLabel }}</span>
                    }
                    <span class="chat-msg__time">{{ formatTime(msg.timestamp) }}</span>
                  </header>
                  @if (msg.role === 'tool') {
                    <button type="button"
                            class="chat-tool__title"
                            (click)="toggleChatMsg(msg.id)">
                      <span class="chat-tool__chevron">{{ isChatExpanded(msg) ? 'v' : '>' }}</span>
                      <span class="chat-tool__name">{{ msg.title }}</span>
                      @if (msg.subtitle) {
                        <span class="chat-tool__sub">{{ msg.subtitle }}</span>
                      }
                      <span class="chat-tool__count">{{ msg.body.length }}</span>
                    </button>
                    @if (isChatExpanded(msg)) {
                      <pre class="chat-tool__body">{{ formatChatBody(msg.body) }}</pre>
                    }
                  } @else {
                    <div class="chat-msg__body">{{ msg.title }}</div>
                    @if (msg.body.length > 1) {
                      <pre class="chat-msg__more">{{ formatChatBody(msg.body.slice(1)) }}</pre>
                    }
                  }
                </div>
              </article>
            }
            @if (chatMessages().length === 0) {
              <div class="activity-log__empty">
                {{ lines().length === 0 ? 'No activity output yet.' : 'No activity entries match the current filters.' }}
              </div>
            }
          </div>
        } @else if (mode() === 'parsed') {
          @for (group of visibleGroups(); track group.id) {
            <article class="activity-group"
                     [class.activity-group--error]="group.status === 'error'"
                     [class.activity-group--neutral]="group.status === 'neutral'">
              <button class="activity-group__header" (click)="toggleGroup(group)">
                <span class="activity-group__chevron">{{ isExpanded(group) ? 'v' : '>' }}</span>
                <span class="activity-group__kind">{{ kindLabel(group.kind) }}</span>
                <span class="activity-group__title">{{ group.title }}</span>
                <span class="activity-group__count">{{ group.lines.length }}</span>
              </button>
              @if (group.subtitle) {
                <div class="activity-group__subtitle">{{ group.subtitle }}</div>
              }
              @if (isExpanded(group)) {
                <div class="activity-group__lines">
                  @for (line of group.lines; track $index) {
                    <div class="activity-line" [class.activity-line--stderr]="line.stream === 'stderr'">
                      <span class="activity-line__time">{{ formatTime(line.timestamp) }}</span>
                      <span class="activity-line__stream">{{ line.stream === 'stderr' ? 'ERR' : 'OUT' }}</span>
                      <span class="activity-line__text">{{ line.text }}</span>
                    </div>
                  }
                </div>
              }
            </article>
          }
        } @else {
          <div class="activity-raw">
            @for (line of visibleLines(); track $index) {
              <div class="activity-line" [class.activity-line--stderr]="line.stream === 'stderr'">
                <span class="activity-line__time">{{ formatTime(line.timestamp) }}</span>
                <span class="activity-line__stream">{{ line.stream === 'stderr' ? 'ERR' : 'OUT' }}</span>
                <span class="activity-line__text">{{ line.text }}</span>
              </div>
            }
          </div>
        }

        @if (mode() !== 'chat' && visibleLines().length === 0) {
          <div class="activity-log__empty">
            {{ lines().length === 0 ? 'No activity output yet.' : 'No activity entries match the current filters.' }}
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
    .activity-log__toolbar,
    .activity-log__filters,
    .activity-log__summary {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .activity-log__toolbar {
      justify-content: space-between;
      background: rgba(255,255,255,0.03);
    }
    .activity-log--embedded .activity-log__toolbar {
      padding: 0 0 6px;
      background: transparent;
    }
    .activity-log--embedded .activity-log__filters {
      padding: 4px 0 6px;
    }
    .activity-log--embedded .activity-log__summary {
      padding: 3px 0 6px;
    }
    .activity-log--embedded .activity-log__body {
      padding: 2px 0 0;
      min-height: 132px;
    }
    .activity-log__tabs,
    .activity-log__actions,
    .activity-log__filters {
      flex-wrap: wrap;
    }
    .activity-log__tab,
    .activity-log__btn {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      border-radius: 4px;
      padding: 4px 9px;
      font-size: 12px;
      cursor: pointer;
    }
    .activity-log__tab--active {
      color: #d8b4fe;
      border-color: rgba(216,180,254,0.35);
      background: rgba(126,34,206,0.18);
    }
    .activity-log__filter {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: #cbd5e1;
      font-size: 12px;
      white-space: nowrap;
    }
    .activity-log__filter input {
      accent-color: #7c3aed;
    }
    .activity-log__summary {
      justify-content: space-between;
      color: #64748b;
      font-size: 11px;
    }
    .activity-log__body {
      flex: 1 1 auto;
      overflow-y: auto;
      padding: 10px;
      min-height: 160px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
      line-height: 1.5;
      position: relative;
      scroll-behavior: smooth;
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
    .activity-log__jump:hover {
      background: rgba(124,58,237,0.85);
      color: #ede9fe;
    }
    .activity-group {
      border: 1px solid rgba(148,163,184,0.16);
      border-left: 3px solid #38bdf8;
      border-radius: 8px;
      margin-bottom: 8px;
      background: rgba(15,23,42,0.5);
      overflow: hidden;
    }
    .activity-log--embedded .activity-group {
      border-radius: 6px;
      margin-bottom: 6px;
      background: rgba(15,23,42,0.38);
    }
    .activity-group--error {
      border-left-color: #fb7185;
      background: rgba(127,29,29,0.18);
    }
    .activity-group--neutral {
      border-left-color: #94a3b8;
    }
    .activity-group__header {
      width: 100%;
      display: grid;
      grid-template-columns: 18px minmax(86px, auto) minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      padding: 8px 10px;
      color: #e2e8f0;
      background: transparent;
      border: 0;
      text-align: left;
      cursor: pointer;
      font: inherit;
    }
    .activity-log--embedded .activity-group__header {
      padding: 6px 8px;
    }
    .activity-group__chevron,
    .activity-group__count {
      color: #94a3b8;
      font-variant-numeric: tabular-nums;
    }
    .activity-group__kind {
      color: #a7f3d0;
      font-size: 10px;
      text-transform: uppercase;
      font-weight: 700;
      letter-spacing: 0.04em;
    }
    .activity-group__title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .activity-group__subtitle {
      padding: 0 10px 8px 126px;
      color: #94a3b8;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .activity-group__lines,
    .activity-raw {
      border-top: 1px solid rgba(255,255,255,0.05);
      padding: 6px 0;
    }
    .activity-line {
      display: grid;
      grid-template-columns: 72px 34px minmax(0, 1fr);
      gap: 8px;
      padding: 2px 10px;
      align-items: baseline;
    }
    .activity-line:hover {
      background: rgba(255,255,255,0.04);
    }
    .activity-line__time {
      color: #64748b;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }
    .activity-line__stream {
      color: #4ade80;
      background: rgba(34,197,94,0.1);
      border-radius: 3px;
      text-align: center;
      font-size: 9px;
      font-weight: 700;
      padding: 1px 4px;
    }
    .activity-line--stderr .activity-line__stream {
      color: #fb7185;
      background: rgba(251,113,133,0.1);
    }
    .activity-line--stderr .activity-line__text {
      color: #fca5a5;
    }
    .activity-line__text {
      color: #e2e8f0;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .activity-log__empty {
      padding: 24px 12px;
      color: #64748b;
      text-align: center;
    }
    .activity-chat {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 4px 2px 8px;
      font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif;
    }
    .chat-msg {
      display: grid;
      grid-template-columns: 30px minmax(0, 1fr);
      gap: 10px;
      align-items: flex-start;
    }
    .chat-msg__avatar {
      width: 30px;
      height: 30px;
      border-radius: 50%;
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 15px;
      background: linear-gradient(135deg, #7c3aed, #4338ca);
      color: #fff;
      box-shadow: 0 1px 4px rgba(76,29,149,0.5);
      user-select: none;
    }
    .chat-msg--tool .chat-msg__avatar {
      background: linear-gradient(135deg, #0ea5e9, #1d4ed8);
      box-shadow: 0 1px 4px rgba(29,78,216,0.45);
    }
    .chat-msg--error .chat-msg__avatar {
      background: linear-gradient(135deg, #f43f5e, #b91c1c);
      box-shadow: 0 1px 4px rgba(190,18,60,0.5);
    }
    .chat-msg__bubble {
      background: rgba(30,41,59,0.65);
      border: 1px solid rgba(148,163,184,0.18);
      border-radius: 12px;
      padding: 8px 12px 9px;
      min-width: 0;
      box-shadow: 0 1px 2px rgba(0,0,0,0.25);
    }
    .chat-msg--agent .chat-msg__bubble {
      background: rgba(76,29,149,0.18);
      border-color: rgba(196,181,253,0.28);
    }
    .chat-msg--tool .chat-msg__bubble {
      background: rgba(15,23,42,0.7);
      border-color: rgba(125,211,252,0.25);
    }
    .chat-msg--error .chat-msg__bubble {
      background: rgba(127,29,29,0.22);
      border-color: rgba(251,113,133,0.45);
    }
    .chat-msg__head {
      display: flex;
      align-items: baseline;
      gap: 8px;
      margin-bottom: 4px;
      flex-wrap: wrap;
    }
    .chat-msg__author {
      color: #e2e8f0;
      font-weight: 600;
      font-size: 12px;
    }
    .chat-msg--agent .chat-msg__author { color: #ddd6fe; }
    .chat-msg--tool .chat-msg__author { color: #bae6fd; }
    .chat-msg--error .chat-msg__author { color: #fecaca; }
    .chat-msg__pill {
      font-size: 9px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: #cbd5e1;
      background: rgba(148,163,184,0.18);
      padding: 1px 6px;
      border-radius: 999px;
      font-weight: 700;
    }
    .chat-msg__pill--error {
      color: #fecaca;
      background: rgba(220,38,38,0.25);
    }
    .chat-msg__time {
      margin-left: auto;
      color: #64748b;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }
    .chat-msg__body {
      color: #e2e8f0;
      font-size: 13px;
      line-height: 1.5;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .chat-msg--agent .chat-msg__body { color: #ede9fe; }
    .chat-msg--error .chat-msg__body { color: #fecaca; }
    .chat-msg__more {
      margin: 6px 0 0;
      padding: 6px 8px;
      background: rgba(2,6,23,0.55);
      border: 1px solid rgba(148,163,184,0.12);
      border-radius: 6px;
      color: #cbd5e1;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11.5px;
      line-height: 1.4;
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 160px;
      overflow: auto;
    }
    .chat-tool__title {
      width: 100%;
      display: grid;
      grid-template-columns: 14px minmax(0, auto) minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      background: transparent;
      border: 0;
      padding: 0;
      cursor: pointer;
      text-align: left;
      color: #cbd5e1;
      font: inherit;
    }
    .chat-tool__chevron { color: #94a3b8; font-size: 11px; }
    .chat-tool__name {
      color: #e2e8f0;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12.5px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .chat-tool__sub {
      color: #94a3b8;
      font-size: 11.5px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chat-tool__count {
      color: #94a3b8;
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    .chat-tool__body {
      margin: 8px 0 0;
      padding: 8px 10px;
      background: rgba(2,6,23,0.7);
      border: 1px solid rgba(148,163,184,0.15);
      border-radius: 6px;
      color: #cbd5e1;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11.5px;
      line-height: 1.45;
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 240px;
      overflow: auto;
    }
    @media (max-width: 720px) {
      .activity-log__toolbar,
      .activity-log__summary {
        align-items: flex-start;
        flex-direction: column;
      }
      .activity-group__header,
      .activity-line {
        grid-template-columns: 1fr;
      }
      .activity-group__subtitle {
        padding-left: 10px;
      }
      .chat-msg {
        grid-template-columns: 26px minmax(0, 1fr);
      }
    }
  `]
})
export class ActivityLogViewComponent implements AfterViewInit, OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly bodyMaxHeight = input('400px');
  readonly variant = input<'framed' | 'embedded'>('framed');
  readonly mode = signal<'parsed' | 'raw' | 'chat'>('parsed');
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;
  readonly filterKinds = activityLogKinds;
  readonly filters = signal<ActivityLogFilters>({ ...defaultActivityLogFilters });
  readonly expanded = signal<Record<string, boolean>>({});
  readonly chatExpanded = signal<Record<string, boolean>>({});
  readonly stickToBottom = signal(true);

  readonly parsedGroups = computed(() => parseActivityLog(this.lines()));
  readonly visibleGroups = computed(() => filterActivityGroups(this.parsedGroups(), this.filters()));
  readonly visibleLines = computed(() => flattenActivityLines(this.visibleGroups()));
  readonly chatMessages = computed(() => buildChatMessages(this.visibleGroups()));

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;

  private readonly autoScrollEffect = effect(() => {
    // Re-run whenever lines, mode, filters, or expansion change.
    this.lines();
    this.mode();
    this.visibleGroups();
    this.expanded();
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

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  copyTooltip(): string {
    const m = this.mode();
    if (m === 'chat') return 'Copy the visible chat transcript to the clipboard';
    if (m === 'raw') return 'Copy all visible raw lines to the clipboard';
    return 'Copy the visible parsed groups to the clipboard';
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
    const m = this.mode();
    if (m === 'chat') {
      const parts: string[] = [];
      for (const msg of this.chatMessages()) {
        const head = `[${this.formatTime(msg.timestamp)}] ${msg.author}` +
          (msg.kindLabel ? ` (${msg.kindLabel})` : '');
        const body = msg.role === 'tool'
          ? `${msg.title}${msg.subtitle ? ` — ${msg.subtitle}` : ''}\n${this.formatChatBody(msg.body)}`
          : msg.body.length > 1
            ? `${msg.title}\n${this.formatChatBody(msg.body.slice(1))}`
            : msg.title;
        parts.push(`${head}\n${body}`);
      }
      return parts.join('\n\n');
    }
    if (m === 'parsed') {
      const parts: string[] = [];
      for (const group of this.visibleGroups()) {
        parts.push(`=== ${this.kindLabel(group.kind)} — ${group.title} ===`);
        if (group.subtitle) parts.push(group.subtitle);
        for (const line of group.lines) {
          const stream = line.stream === 'stderr' ? 'ERR' : 'OUT';
          parts.push(`[${this.formatTime(line.timestamp)}] ${stream} ${line.text}`);
        }
        parts.push('');
      }
      return parts.join('\n').trimEnd();
    }
    return this.visibleLines()
      .map((line) => {
        const stream = line.stream === 'stderr' ? 'ERR' : 'OUT';
        return `[${this.formatTime(line.timestamp)}] ${stream} ${line.text}`;
      })
      .join('\n');
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
      // Release the suppression on the next frame so programmatic scroll
      // doesn't toggle stick-to-bottom off via the scroll event.
      requestAnimationFrame(() => { this.suppressScrollEvent = false; });
    });
  }

  kindLabel(kind: ActivityLogKind): string {
    return activityKindLabel(kind);
  }

  toggleFilter(kind: ActivityLogKind): void {
    this.filters.update((filters) => ({ ...filters, [kind]: !filters[kind] }));
  }

  isExpanded(group: ActivityLogGroup): boolean {
    return this.expanded()[group.id] ?? !group.collapsedByDefault;
  }

  toggleGroup(group: ActivityLogGroup): void {
    const next = !this.isExpanded(group);
    this.expanded.update((expanded) => ({ ...expanded, [group.id]: next }));
  }

  expandAll(): void {
    const expanded: Record<string, boolean> = {};
    for (const group of this.visibleGroups()) {
      expanded[group.id] = true;
    }
    this.expanded.set(expanded);
  }

  collapseAll(): void {
    const expanded: Record<string, boolean> = {};
    for (const group of this.visibleGroups()) {
      expanded[group.id] = false;
    }
    this.expanded.set(expanded);
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  isChatExpanded(msg: ChatMessage): boolean {
    return this.chatExpanded()[msg.id] ?? !msg.collapsedByDefault;
  }

  toggleChatMsg(id: string): void {
    const msg = this.chatMessages().find((m) => m.id === id);
    if (!msg) return;
    const next = !this.isChatExpanded(msg);
    this.chatExpanded.update((expanded) => ({ ...expanded, [id]: next }));
  }

  formatChatBody(lines: CliOutputLine[]): string {
    return lines.map((line) => line.text).join('\n');
  }
}
