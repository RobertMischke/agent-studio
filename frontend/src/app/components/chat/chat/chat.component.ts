import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MarkdownImageLightboxDirective } from '../../../directives/markdown-image-lightbox.directive';
import { MarkdownViewComponent } from '../../markdown-view/markdown-view.component';
import { mergeByTimestamp } from '../merge-by-timestamp';
import { TooltipDirective } from '../../tooltip';
import {
  ChatDraftAttachment,
  ChatEvent,
  ChatMessage,
  ChatRole,
  ChatSubmitEvent,
  ChatToolbarItem
} from '../chat-types';
import {
  RoleBadgeComponent,
  groupIntoPhases,
  groupIntoSuperPhases,
  type ChatPhase,
  type SuperPhase,
  type PhaseInputMessage,
} from '../../../features/workforce';

interface RenderedMessage {
  kind: 'message';
  id: string;
  /** Sort key used to merge with events chronologically. */
  timestamp: string;
  /**
   * Pre-formatted clock label (HH:MM). Computed once when `rendered()`
   * recomputes — never per change-detection pass — so a keystroke in the
   * composer (which dirties this view) does not re-run the expensive
   * `toLocaleTimeString`/Intl path for every row. See the typing-perf note.
   */
  formattedTime: string;
  message: ChatMessage;
  /** True when the message body exceeds COLLAPSE_LINE_THRESHOLD lines. */
  collapsible: boolean;
  /** Resolved collapsed state: collapsible AND not user-expanded. */
  collapsed: boolean;
  /**
   * F7: true when this is an error message that belongs to an older
   * super-phase (i.e. session). Stale errors get a dimmed look so the
   * operator can tell "history" apart from "live failure".
   */
  staleError: boolean;
}

interface RenderedEvent {
  kind: 'event';
  id: string;
  timestamp: string;
  /** Pre-formatted clock label — see {@link RenderedMessage.formattedTime}. */
  formattedTime: string;
  /** Pre-resolved head glyph; precomputed so the loop body stays binding-only. */
  icon: string;
  /** Pre-resolved kind label; precomputed for the same reason. */
  label: string;
  event: ChatEvent;
  /** True when the event has a markdown detail body to expand. */
  hasDetail: boolean;
  expanded: boolean;
  /** F7: error/warn events older than the latest super-phase get dimmed. */
  staleError: boolean;
}

type RenderedItem = RenderedMessage | RenderedEvent;

/**
 * Source-line threshold above which non-user turns auto-collapse with a
 * "show more" caret. Tuned to roughly two screens of agent prose at the
 * chat's 1.55 line-height; under it, even chatty agents look fine inline.
 */
const COLLAPSE_LINE_THRESHOLD = 24;

/**
 * Reusable chat surface. Pure presentation layer: owns the draft and
 * attachment-staging state and emits `submit`; the host wires that up to
 * a backend. Roles render with distinct Catppuccin-flavoured bubbles
 * (matching activity-log-view so the look is consistent across the app).
 *
 * Inputs cover the parts that vary per surface (placeholder, empty state,
 * disabled while sending). Outputs are minimal: `submit` carries text and
 * the staged attachments. The host is responsible for uploading those
 * attachments and rewriting the text into the final message it persists.
 *
 * Why a separate component instead of folding into activity-log-view: the
 * activity log is a rendering of past run output and has no input field;
 * a chat is bidirectional. Mixing the two would muddy both.
 */
@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [FormsModule, RoleBadgeComponent, MarkdownImageLightboxDirective, MarkdownViewComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent implements AfterViewInit, OnDestroy {
  readonly messages = input<ChatMessage[]>([]);
  readonly events = input<ChatEvent[]>([]);
  readonly placeholder = input<string>('Type a message…');
  readonly emptyState = input<string>('No messages yet.');
  readonly submitLabel = input<string>('Send');
  readonly bodyMaxHeight = input<string>('100%');
  readonly disabled = input<boolean>(false);
  readonly pending = input<boolean>(false);
  readonly variant = input<'framed' | 'embedded'>('framed');
  readonly allowAttachments = input<boolean>(true);
  readonly maxAttachmentBytes = input<number>(10 * 1024 * 1024);

  /**
   * Buttons rendered on the left of the composer's toolbar row above
   * the textarea. Hosts plug in chat-surface-specific affordances
   * (e.g. `#` to reference a task, `@` to mention, fork to start a
   * new thread, search). Clicking emits `toolbarAction({id})`.
   * Empty by default — the toolbar row only renders if either
   * `toolbarStart`, `toolbarEnd`, or `routingLabel` is set.
   */
  readonly toolbarStart = input<readonly ChatToolbarItem[]>([]);
  /** Right-side toolbar items (e.g. `/task` quick action). */
  readonly toolbarEnd = input<readonly ChatToolbarItem[]>([]);
  /**
   * Routing/status chip rendered right-aligned in the toolbar row,
   * e.g. "routing: Codex (Claude paused)". The chat does not interpret
   * the string; it is just an at-a-glance affordance for the host to
   * surface which model/agent will receive the next submit.
   */
  readonly routingLabel = input<string | null>(null);

  /** Emitted when the user clicks a toolbar button by id. */
  readonly toolbarAction = output<{ id: string }>();

  /**
   * When true, only the rows inside (or near) the scroll viewport are
   * rendered — top/bottom spacer divs hold the rest of the scroll
   * height so the scroll bar reflects the full timeline. Stays at ~150
   * DOM nodes regardless of how many thousand turns the chat carries.
   *
   * Off by default to keep small-N hosts simple. Hosts with deep
   * history (project chat, long-running task chats) should switch it
   * on so the chat can grow without the browser stalling.
   */
  readonly virtualised = input<boolean>(false);
  /** Estimated row height in px. Tuned for typical turns + event cards. */
  readonly virtualRowHeightPx = input<number>(120);
  /** Over-scan rows above + below the viewport to smooth the scroll. */
  readonly virtualBufferRows = input<number>(20);

  readonly submitMessage = output<ChatSubmitEvent>();
  /**
   * Slice E: emitted when the user clicks an inline event card's
   * action affordance (e.g. "Open task" on a /bug confirmation card).
   * The host uses the event id to look up the right payload it queued
   * and routes the click in-app rather than via a new browser tab.
   */
  readonly eventAction = output<{ eventId: string }>();

  readonly drafts = signal<ChatDraftAttachment[]>([]);
  readonly attachmentError = signal<string | null>(null);
  readonly stickToBottom = signal(true);
  readonly isDragging = signal(false);
  /** Per-message-id override: ids the user has explicitly expanded. */
  readonly expandedIds = signal<ReadonlySet<string>>(new Set());
  /** Per-event-id override: ids of events the user has expanded. */
  readonly expandedEventIds = signal<ReadonlySet<string>>(new Set());

  draftText = '';

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly inputRef = viewChild<ElementRef<HTMLTextAreaElement>>('input');
  private readonly fileInputRef = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;

  /**
   * Chat phases derived from the merged message stream. The chat
   * component already orders by timestamp; we feed the same source
   * directly into the grouping helper so the dividers line up exactly
   * with what the verbatim feed shows.
   */
  readonly phases = computed<ChatPhase[]>(() => {
    const input: PhaseInputMessage[] = this.messages().map((m) => ({
      id: m.id,
      ts: m.timestamp,
      author: m.role,
    }));
    return groupIntoPhases(input);
  });

  /**
   * Super-phases — outer grouping. A new super-phase opens when there
   * is an idle gap of ≥ 15 min between two phases (rule lives in
   * {@link groupIntoSuperPhases}). All phases otherwise belong to the
   * same super-phase, so a single active conversation just paints one
   * "Session" header at the top.
   */
  readonly superPhases = computed<SuperPhase[]>(() =>
    groupIntoSuperPhases(this.phases())
  );

  /** First index of the rendered() slice that the template should draw
   *  when virtualisation is on. */
  readonly visibleStart = signal<number>(0);
  /** Exclusive end index of the visible slice (clamped to length). */
  readonly visibleEnd = signal<number>(50);

  readonly rendered = computed<RenderedItem[]>(() => {
    const expanded = this.expandedIds();
    const expandedEvents = this.expandedEventIds();
    // F7: cutoff = start ts of the latest super-phase. Errors before
    // this point belong to a previous session and render dimmed so
    // the operator can tell historical failures apart from a live one.
    const superPhases = this.superPhases();
    const staleCutoffMs = superPhases.length > 0
      ? Date.parse(superPhases[superPhases.length - 1].startTs)
      : Number.NEGATIVE_INFINITY;
    const isStaleError = (ts: string, hasError: boolean): boolean => {
      if (!hasError) return false;
      const t = Date.parse(ts);
      return Number.isFinite(t) && Number.isFinite(staleCutoffMs) && t < staleCutoffMs;
    };
    const messageItems: RenderedItem[] = this.messages().map((message) => {
      // Every chat turn can carry Markdown. Route all roles through the
      // canonical <app-markdown> renderer so operator-pasted tables/lists and
      // orchestrator replies render with the same GFM, code, link, and
      // sanitisation path used elsewhere.
      const isUser = message.role === 'user';
      // Source-line count is a cheap, deterministic proxy for visual height
      // that survives signal recomputes; we don't need exact rendered geometry
      // for the collapse decision since the CSS max-height clips below the fold.
      const sourceLines = countSourceLines(message.text);
      const collapsible = !isUser && !message.pending && sourceLines > COLLAPSE_LINE_THRESHOLD;
      const collapsed = collapsible && !expanded.has(message.id);
      return {
        kind: 'message',
        id: message.id,
        timestamp: message.timestamp,
        formattedTime: this.formatTime(message.timestamp),
        message,
        collapsible,
        collapsed,
        staleError: isStaleError(message.timestamp, !!message.error),
      };
    });
    const eventItems: RenderedItem[] = this.events().map((event) => ({
      kind: 'event',
      id: event.id,
      timestamp: event.timestamp,
      formattedTime: this.formatTime(event.timestamp),
      icon: this.eventIcon(event),
      label: this.eventLabel(event),
      event,
      hasDetail: !!event.detail,
      expanded: expandedEvents.has(event.id),
      staleError: isStaleError(event.timestamp, event.severity === 'error' || event.severity === 'warn'),
    }));
    // F15: phase / super-phase dividers no longer render inline in the
    // chat stream — in the orchestrator chat every "phase" is a single
    // Q-A pair, so the bracket is visual noise. The `phases()` and
    // `superPhases()` computed signals stay alive: they still drive
    // the F7 stale-error cutoff above, and the verbose-debug overlay's
    // Phases tab computes its own grouping over the same shape.
    return mergeByTimestamp(messageItems, eventItems);
  });

  /**
   * Rendered() slice the template actually loops over when virtualised
   * mode is on. In non-virtualised mode this just returns the full
   * rendered() array — callers can use it unconditionally.
   */
  readonly windowedItems = computed<RenderedItem[]>(() => {
    const items = this.rendered();
    if (!this.virtualised()) return items;
    const start = Math.max(0, Math.min(this.visibleStart(), items.length));
    const end   = Math.max(start, Math.min(this.visibleEnd(), items.length));
    return items.slice(start, end);
  });
  /** Top-spacer height keeping scroll position correct in virtual mode. */
  readonly topSpacerPx = computed<number>(() => {
    if (!this.virtualised()) return 0;
    return Math.max(0, this.visibleStart()) * this.virtualRowHeightPx();
  });
  /** Bottom-spacer height for rows below the visible window. */
  readonly bottomSpacerPx = computed<number>(() => {
    if (!this.virtualised()) return 0;
    const total = this.rendered().length;
    return Math.max(0, total - this.visibleEnd()) * this.virtualRowHeightPx();
  });

  /**
   * Keep visibleEnd within bounds as rendered() grows (new turns
   * arrive) and seed the visible window when virtualisation is first
   * enabled. The actual scroll-driven update happens inside
   * onBodyScroll; this effect just makes sure the initial slice is
   * sensible and that pushing new messages doesn't leave visibleEnd
   * pointing past the array.
   */
  private readonly virtualBoundsEffect = effect(() => {
    if (!this.virtualised()) return;
    const total = this.rendered().length;
    const buffer = this.virtualBufferRows();
    const sticky = this.stickToBottom();
    untracked(() => {
      // When the user is at the bottom (sticky), keep visibleEnd at the
      // end so new turns appear without manual scroll. Otherwise clamp.
      if (sticky) {
        const winSize = Math.max(50, this.visibleEnd() - this.visibleStart());
        this.visibleEnd.set(total);
        this.visibleStart.set(Math.max(0, total - winSize));
      } else {
        this.visibleEnd.set(Math.min(this.visibleEnd(), total));
        this.visibleStart.set(Math.min(this.visibleStart(), Math.max(0, total - buffer)));
      }
    });
  });

  private readonly autoScrollEffect = effect(() => {
    this.messages();
    this.events();
    this.pending();
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
    for (const draft of this.drafts()) URL.revokeObjectURL(draft.previewUrl);
    this.autoScrollEffect.destroy();
    this.virtualBoundsEffect.destroy();
  }

  canSend(): boolean {
    return this.draftText.trim().length > 0 || this.drafts().length > 0;
  }

  roleLabel(role: ChatRole): string {
    switch (role) {
      case 'user': return 'You';
      case 'agent': return 'Agent';
      case 'orchestrator': return '⚙ Orchestrator';
      case 'system': return 'System';
    }
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    } catch {
      return iso;
    }
  }

  onSubmit(event: Event): void {
    event.preventDefault();
    if (this.disabled() || !this.canSend()) return;
    const text = this.draftText.trim();
    const attachments = this.drafts();
    this.submitMessage.emit({ text, attachments });
    this.draftText = '';
    this.drafts.set([]);
    this.attachmentError.set(null);
    this.stickToBottom.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  onInputKeydown(event: KeyboardEvent): void {
    // Enter to send, Shift+Enter for newline. Ctrl/Cmd+Enter also sends so the
    // user can submit even from inside a multi-line draft without losing the
    // newline shortcut.
    if (event.key !== 'Enter') return;
    if (event.shiftKey) return;
    if (event.isComposing) return;
    event.preventDefault();
    this.onSubmit(event);
  }

  onPaste(event: ClipboardEvent): void {
    if (!this.allowAttachments()) return;
    const file = imageFromClipboard(event.clipboardData);
    if (!file) return;
    event.preventDefault();
    this.addAttachment(file);
  }

  onDragOver(event: DragEvent): void {
    if (!this.allowAttachments()) return;
    if (!event.dataTransfer) return;
    if (!Array.from(event.dataTransfer.types).includes('Files')) return;
    event.preventDefault();
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent): void {
    if (event.target !== event.currentTarget) return;
    this.isDragging.set(false);
  }

  onDrop(event: DragEvent): void {
    this.isDragging.set(false);
    if (!this.allowAttachments()) return;
    const files = Array.from(event.dataTransfer?.files ?? []).filter((f) => f.type.startsWith('image/'));
    if (files.length === 0) return;
    event.preventDefault();
    for (const file of files) this.addAttachment(file);
  }

  triggerFilePicker(): void {
    this.fileInputRef()?.nativeElement.click();
  }

  onFileInputChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    const files = Array.from(target.files ?? []);
    for (const file of files) {
      if (file.type.startsWith('image/')) this.addAttachment(file);
    }
    target.value = '';
  }

  removeDraftAttachment(id: string): void {
    const list = this.drafts();
    const found = list.find((a) => a.id === id);
    if (found) URL.revokeObjectURL(found.previewUrl);
    this.drafts.set(list.filter((a) => a.id !== id));
  }

  private addAttachment(file: File): void {
    if (file.size > this.maxAttachmentBytes()) {
      const mb = Math.round(this.maxAttachmentBytes() / (1024 * 1024));
      this.attachmentError.set(`Image too large (max ${mb} MB).`);
      return;
    }
    this.attachmentError.set(null);
    const id = makeId();
    const alt = deriveAlt(file);
    const previewUrl = URL.createObjectURL(file);
    this.drafts.set([...this.drafts(), { id, file, alt, previewUrl }]);
  }

  onBodyScroll(): void {
    if (this.suppressScrollEvent) return;
    const el = this.bodyRef()?.nativeElement;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    const sticky = distanceFromBottom <= 24;
    this.stickToBottom.set(sticky);

    if (!this.virtualised()) return;
    const total = this.rendered().length;
    if (sticky) {
      // At the bottom: the window is owned by virtualBoundsEffect, which
      // pins it to the tail. Do NOT re-derive it from scrollTop here. The
      // row-height estimate (virtualRowHeightPx) is much taller than short
      // orchestrator turns, so a scroll-derived window would land a phantom
      // bottom spacer under the freshly loaded tail and push it out of the
      // viewport — the "content vanishes after load, reappears on scroll"
      // bug (ASS-613 sibling). Scroll-anchoring reflow during the side-sheet
      // open animation, async markdown growth, or the programmatic pin's own
      // event all fire a scroll while sticky; keep the tail pinned for each.
      const winSize = Math.max(50, this.visibleEnd() - this.visibleStart());
      this.visibleEnd.set(total);
      this.visibleStart.set(Math.max(0, total - winSize));
      return;
    }
    const rowH = Math.max(1, this.virtualRowHeightPx());
    const buffer = this.virtualBufferRows();
    const firstVisibleRow = Math.floor(el.scrollTop / rowH);
    const visibleRows = Math.ceil(el.clientHeight / rowH);
    const start = Math.max(0, firstVisibleRow - buffer);
    const end = Math.min(total, firstVisibleRow + visibleRows + buffer);
    this.visibleStart.set(start);
    this.visibleEnd.set(end);
  }

  jumpToBottom(): void {
    this.stickToBottom.set(true);
    this.scheduleScrollToBottom();
  }

  toggleCollapsed(messageId: string): void {
    const next = new Set(this.expandedIds());
    if (next.has(messageId)) {
      next.delete(messageId);
    } else {
      next.add(messageId);
    }
    this.expandedIds.set(next);
  }

  onEventAction(event: Event, eventId: string): void {
    event.preventDefault();
    event.stopPropagation();
    this.eventAction.emit({ eventId });
  }

  onToolbarAction(id: string): void {
    this.toolbarAction.emit({ id });
  }

  /** True when at least one of the toolbar slots has content. */
  readonly toolbarVisible = computed<boolean>(() => {
    return this.toolbarStart().length > 0
      || this.toolbarEnd().length > 0
      || this.routingLabel() !== null;
  });

  toggleEventExpanded(eventId: string): void {
    const next = new Set(this.expandedEventIds());
    if (next.has(eventId)) {
      next.delete(eventId);
    } else {
      next.add(eventId);
    }
    this.expandedEventIds.set(next);
  }

  eventIcon(event: ChatEvent): string {
    // F15: decision events pick a subtype-specific glyph so the
    // inline orchestrator card communicates the verdict at a glance
    // without forcing the operator to expand the detail body.
    if (event.kind === 'decision') return decisionIcon(event.decisionType);
    switch (event.kind) {
      case 'tool-call':         return '🔧';
      case 'watchdog':          return '⏱';
      case 'rate-limit':        return '⏳';
      case 'update':            return '↻';
      case 'task':              return '🎯';
      case 'session-recovered': return '⟳';
      case 'memory-refreshed':  return '⊕';
    }
    return '•';
  }

  eventLabel(event: ChatEvent): string {
    // F15: render decision events as "Orchestrator: <decisionType>" so
    // the kind chip is self-describing in the merged chat stream.
    if (event.kind === 'decision') {
      const sub = (event.decisionType ?? '').trim();
      return sub ? `Orchestrator: ${sub}` : 'Orchestrator';
    }
    switch (event.kind) {
      case 'tool-call':         return 'Tool call';
      case 'watchdog':          return 'Watchdog';
      case 'rate-limit':        return 'Rate limit';
      case 'update':            return 'Update';
      case 'task':              return 'Task';
      case 'session-recovered': return 'Session recovered';
      case 'memory-refreshed':  return 'Memory refreshed';
    }
    return '';
  }

  private scheduleScrollToBottom(): void {
    if (typeof requestAnimationFrame === 'undefined') return;
    // Coalesce to a single pin per frame. A poll tick can fire the
    // autoscroll effect several times (new message + event + pending
    // arrays all land together), but we only want one scrollTop write,
    // after Angular has rendered the new rows — never multiple per tick.
    if (this.scrollFrame !== null) cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = null;
      const el = this.bodyRef()?.nativeElement;
      if (!el) return;
      // The user may have scrolled up between scheduling and this frame;
      // honour that and never yank them back to the bottom once they've
      // left it. The autoscroll effect re-schedules us when they return.
      if (!this.stickToBottom()) return;
      // Suppress the scroll event this write provokes so onBodyScroll
      // doesn't misread the position and flip stickToBottom. The write
      // is instant (the body intentionally has no smooth scroll-behavior),
      // so it fires exactly one scroll event — cleared on the next frame.
      this.suppressScrollEvent = true;
      el.scrollTop = el.scrollHeight;
      requestAnimationFrame(() => { this.suppressScrollEvent = false; });
    });
  }
}

function imageFromClipboard(data: DataTransfer | null): File | null {
  if (!data) return null;
  for (const item of Array.from(data.items)) {
    if (item.kind === 'file' && item.type.startsWith('image/')) {
      const file = item.getAsFile();
      if (file) return file;
    }
  }
  for (const file of Array.from(data.files ?? [])) {
    if (file.type.startsWith('image/')) return file;
  }
  return null;
}

function deriveAlt(file: File): string {
  const stem = (file.name ?? '').replace(/\.[^.]+$/, '').trim();
  return stem || 'screenshot';
}

function makeId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return Math.random().toString(36).slice(2, 14);
}

/**
 * Decision-event glyph dispatcher. The orchestrator currently emits four
 * `decisionType` values (see `OrchestratorChatLog.cs`); each one gets a
 * distinct one-character chrome so a glance at the chat stream tells the
 * operator what happened without expanding the detail body:
 *   `decision`  → ⚙  (a deliberate verdict)
 *   `reissue`   → ↻  (re-run with stronger framing)
 *   `heuristic` → ◌  (fallback, low confidence)
 *   `giveup`    → ⊘  (terminal; ask the human)
 * Unknown subtypes fall back to ⚙ so a new backend kind never renders
 * as the empty string in the head row.
 */
function decisionIcon(decisionType: string | undefined): string {
  switch ((decisionType ?? '').toLowerCase()) {
    case 'reissue':   return '↻';
    case 'heuristic': return '◌';
    case 'giveup':    return '⊘';
    case 'decision':  return '⚙';
    default:          return '⚙';
  }
}

function countSourceLines(text: string): number {
  if (!text) return 0;
  // Newline-separated source lines; trailing newlines don't count as a row.
  return text.replace(/\n+$/, '').split('\n').length;
}
