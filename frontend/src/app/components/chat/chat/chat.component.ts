import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { markdownToHtml } from '../../markdown-utils';
import { MarkdownImageLightboxDirective } from '../../../directives/markdown-image-lightbox.directive';
import { mergeByTimestamp } from '../merge-by-timestamp';
import { TooltipDirective } from '../../tooltip';
import {
  ChatDraftAttachment,
  ChatEvent,
  ChatEventKind,
  ChatMessage,
  ChatRole,
  ChatSubmitEvent,
  ChatToolbarItem
} from '../chat-types';
import {
  RoleBadgeComponent,
  PhaseSummaryListComponent,
  groupIntoPhases,
  type ChatPhase,
  type PhaseInputMessage,
} from '../../../features/workforce';

interface RenderedMessage {
  kind: 'message';
  id: string;
  /** Sort key used to merge with events chronologically. */
  timestamp: string;
  message: ChatMessage;
  bodyHtml: SafeHtml;
  /** True when the message body exceeds COLLAPSE_LINE_THRESHOLD lines. */
  collapsible: boolean;
  /** Resolved collapsed state: collapsible AND not user-expanded. */
  collapsed: boolean;
}

interface RenderedEvent {
  kind: 'event';
  id: string;
  timestamp: string;
  event: ChatEvent;
  /** Pre-rendered markdown for the expanded detail body. */
  detailHtml: SafeHtml | null;
  expanded: boolean;
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
  imports: [FormsModule, RoleBadgeComponent, PhaseSummaryListComponent, MarkdownImageLightboxDirective, TooltipDirective],
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
   * When true (default), the phase-summary above the chat collapses to
   * a single "▸ N earlier phases" strip the user can click to expand.
   * Stops the chat panel from looking like a flat phase-summary table
   * when there is a lot of history. Pass `false` to keep every phase
   * row visible at the top.
   */
  readonly compactPhaseSummary = input<boolean>(true);

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
  /** Per-phase-id override: explicit expand/collapse from the operator. */
  readonly phaseOverrides = signal<ReadonlyMap<string, boolean>>(new Map());

  draftText = '';

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly inputRef = viewChild<ElementRef<HTMLTextAreaElement>>('input');
  private readonly fileInputRef = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;

  private readonly sanitizer = inject(DomSanitizer);

  /**
   * Chat phases derived from the merged message stream. The chat
   * component already orders by timestamp; we feed the same source
   * directly into the grouping helper so the summary lines line up
   * exactly with what the verbatim feed shows.
   */
  readonly phases = computed<ChatPhase[]>(() => {
    const input: PhaseInputMessage[] = this.messages().map((m) => ({
      id: m.id,
      ts: m.timestamp,
      author: m.role,
    }));
    return groupIntoPhases(input);
  });

  readonly expandedPhaseIds = computed<ReadonlySet<string>>(() => {
    const phases = this.phases();
    const overrides = this.phaseOverrides();
    if (overrides.size === 0) {
      if (phases.length === 0) return new Set();
      return new Set([phases[phases.length - 1].id]);
    }
    const baseline = new Set<string>();
    if (phases.length > 0) baseline.add(phases[phases.length - 1].id);
    for (const [id, expanded] of overrides) {
      if (expanded) baseline.add(id);
      else baseline.delete(id);
    }
    return baseline;
  });

  readonly hiddenMessageIds = computed<ReadonlySet<string>>(() => {
    const expanded = this.expandedPhaseIds();
    const hidden = new Set<string>();
    for (const phase of this.phases()) {
      if (expanded.has(phase.id)) continue;
      for (const id of phase.messageIds) hidden.add(id);
    }
    return hidden;
  });

  readonly rendered = computed<RenderedItem[]>(() => {
    const expanded = this.expandedIds();
    const expandedEvents = this.expandedEventIds();
    const hiddenIds = this.hiddenMessageIds();
    const messageItems: RenderedItem[] = this.messages()
      .filter((message) => !hiddenIds.has(message.id))
      .map((message) => {
      // User input stays plain text (newlines + escaping); every other role
      // ships Markdown, which is how agents and orchestrator log entries
      // express themselves on the wire.
      const isUser = message.role === 'user';
      const bodyHtml = this.sanitizer.bypassSecurityTrustHtml(
        isUser
          ? escapeForPlain(message.text)
          : markdownToHtml(message.text, { codeLineNumbers: true })
      );
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
        message,
        bodyHtml,
        collapsible,
        collapsed
      };
    });
    const eventItems: RenderedItem[] = this.events().map((event) => ({
      kind: 'event',
      id: event.id,
      timestamp: event.timestamp,
      event,
      detailHtml: event.detail
        ? this.sanitizer.bypassSecurityTrustHtml(
            markdownToHtml(event.detail, { codeLineNumbers: true })
          )
        : null,
      expanded: expandedEvents.has(event.id)
    }));
    return mergeByTimestamp(messageItems, eventItems);
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
    this.stickToBottom.set(distanceFromBottom <= 24);
  }

  jumpToBottom(): void {
    this.stickToBottom.set(true);
    this.scheduleScrollToBottom();
  }

  onPhaseToggled(event: { phaseId: string; expanded: boolean }): void {
    const next = new Map(this.phaseOverrides());
    next.set(event.phaseId, event.expanded);
    this.phaseOverrides.set(next);
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

  eventIcon(kind: ChatEventKind): string {
    switch (kind) {
      case 'tool-call':         return '🔧';
      case 'watchdog':          return '⏱';
      case 'rate-limit':        return '⏳';
      case 'decision':          return '⚙';
      case 'update':            return '↻';
      case 'task':              return '🎯';
      case 'session-recovered': return '⟳';
      case 'memory-refreshed':  return '⊕';
    }
  }

  eventLabel(kind: ChatEventKind): string {
    switch (kind) {
      case 'tool-call':         return 'Tool call';
      case 'watchdog':          return 'Watchdog';
      case 'rate-limit':        return 'Rate limit';
      case 'decision':          return 'Decision';
      case 'update':            return 'Update';
      case 'task':              return 'Task';
      case 'session-recovered': return 'Session recovered';
      case 'memory-refreshed':  return 'Memory refreshed';
    }
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

function countSourceLines(text: string): number {
  if (!text) return 0;
  // Newline-separated source lines; trailing newlines don't count as a row.
  return text.replace(/\n+$/, '').split('\n').length;
}

function escapeForPlain(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}
