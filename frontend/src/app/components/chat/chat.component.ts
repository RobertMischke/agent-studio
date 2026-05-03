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
import { markdownToHtml } from '../markdown-utils';
import {
  ChatDraftAttachment,
  ChatMessage,
  ChatRole,
  ChatSubmitEvent
} from './chat-types';

interface RenderedMessage {
  message: ChatMessage;
  bodyHtml: SafeHtml;
}

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
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chat" [class.chat--embedded]="variant() === 'embedded'">
      <div #body
           class="chat__body"
           [style.max-height]="bodyMaxHeight()"
           data-testid="chat-body"
           (scroll)="onBodyScroll()">
        @if (!stickToBottom()) {
          <button type="button"
                  class="chat__jump"
                  data-testid="chat-jump-bottom"
                  (click)="jumpToBottom()">↓ Jump to latest</button>
        }

        @if (rendered().length === 0) {
          <div class="chat__empty" data-testid="chat-empty">{{ emptyState() }}</div>
        } @else {
          @for (item of rendered(); track item.message.id) {
            <article class="chat__msg"
                     [class.chat__msg--user]="item.message.role === 'user'"
                     [class.chat__msg--agent]="item.message.role === 'agent'"
                     [class.chat__msg--orchestrator]="item.message.role === 'orchestrator'"
                     [class.chat__msg--system]="item.message.role === 'system'"
                     [class.chat__msg--pending]="item.message.pending"
                     [class.chat__msg--error]="!!item.message.error"
                     [attr.data-testid]="'chat-msg-' + item.message.role">
              <header class="chat__msg-head">
                <span class="chat__msg-role">{{ roleLabel(item.message.role) }}</span>
                <span class="chat__msg-time">{{ formatTime(item.message.timestamp) }}</span>
              </header>
              <div class="chat__msg-body"
                   [class.chat__msg-body--markdown]="item.message.role !== 'user'"
                   [innerHTML]="item.bodyHtml"></div>
              @if (item.message.attachments?.length) {
                <ul class="chat__msg-attachments">
                  @for (att of item.message.attachments; track att.url) {
                    <li class="chat__msg-attachment" [class.chat__msg-attachment--pending]="att.pending">
                      <img [src]="att.url" [alt]="att.alt" />
                      <span class="chat__msg-attachment-name">{{ att.alt }}</span>
                    </li>
                  }
                </ul>
              }
              @if (item.message.error) {
                <div class="chat__msg-error">{{ item.message.error }}</div>
              }
            </article>
          }
          @if (pending()) {
            <div class="chat__typing" data-testid="chat-typing">
              <span></span><span></span><span></span>
            </div>
          }
        }
      </div>

      <form class="chat__composer"
            [class.chat__composer--drag]="isDragging()"
            (submit)="onSubmit($event)"
            (dragover)="onDragOver($event)"
            (dragleave)="onDragLeave($event)"
            (drop)="onDrop($event)">

        @if (drafts().length > 0) {
          <ul class="chat__drafts" data-testid="chat-drafts">
            @for (att of drafts(); track att.id) {
              <li class="chat__draft">
                <img [src]="att.previewUrl" [alt]="att.alt" />
                <span class="chat__draft-name">{{ att.alt }}</span>
                <button type="button"
                        class="chat__draft-remove"
                        title="Remove attachment"
                        (click)="removeDraftAttachment(att.id)">×</button>
              </li>
            }
          </ul>
        }

        @if (attachmentError()) {
          <div class="chat__attachment-error">{{ attachmentError() }}</div>
        }

        <div class="chat__composer-row">
          <textarea #input
                    class="chat__input"
                    data-testid="chat-input"
                    rows="2"
                    [placeholder]="placeholder()"
                    [disabled]="disabled()"
                    [(ngModel)]="draftText"
                    [ngModelOptions]="{ standalone: true }"
                    (paste)="onPaste($event)"
                    (keydown)="onInputKeydown($event)"></textarea>

          <div class="chat__composer-actions">
            @if (allowAttachments()) {
              <button type="button"
                      class="chat__icon-btn"
                      title="Attach image"
                      data-testid="chat-attach"
                      [disabled]="disabled()"
                      (click)="triggerFilePicker()">📎</button>
              <input #fileInput
                     type="file"
                     accept="image/*"
                     multiple
                     class="chat__file-input"
                     (change)="onFileInputChange($event)" />
            }
            <button type="submit"
                    class="chat__send-btn"
                    data-testid="chat-send"
                    [disabled]="disabled() || !canSend()">
              {{ pending() ? '…' : submitLabel() }}
            </button>
          </div>
        </div>
      </form>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
    }
    .chat {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
      background: #0d0d1a;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 12px;
      overflow: hidden;
    }
    .chat--embedded {
      background: transparent;
      border: 0;
      border-radius: 0;
    }

    .chat__body {
      flex: 1 1 auto;
      overflow-y: auto;
      padding: 14px;
      min-height: 160px;
      display: flex;
      flex-direction: column;
      gap: 12px;
      font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif;
      font-size: 13.5px;
      line-height: 1.55;
      scroll-behavior: smooth;
      position: relative;
    }
    .chat__jump {
      position: sticky;
      top: 0;
      align-self: flex-end;
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
    .chat__empty {
      padding: 28px 12px;
      color: #64748b;
      text-align: center;
      font-style: italic;
    }

    .chat__msg {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 10px 14px 12px;
      border-radius: 12px;
      border: 1px solid rgba(148,163,184,0.16);
      background: rgba(15,23,42,0.55);
    }
    .chat__msg--agent {
      background: rgba(76,29,149,0.16);
      border-color: rgba(196,181,253,0.28);
    }
    .chat__msg--user {
      background: rgba(13,148,136,0.16);
      border-color: rgba(94,234,212,0.32);
    }
    .chat__msg--system {
      background: rgba(127,29,29,0.18);
      border-color: rgba(251,113,133,0.4);
    }
    .chat__msg--orchestrator {
      background: rgba(120,113,108,0.18);
      border-color: rgba(217,119,6,0.45);
    }
    .chat__msg--pending { opacity: 0.75; }
    .chat__msg--error { border-color: rgba(248,113,113,0.55); }

    .chat__msg-head {
      display: flex;
      align-items: baseline;
      gap: 8px;
      font-size: 11px;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      font-weight: 700;
      color: #94a3b8;
    }
    .chat__msg--agent .chat__msg-head { color: #ddd6fe; }
    .chat__msg--user .chat__msg-head { color: #99f6e4; }
    .chat__msg--orchestrator .chat__msg-head { color: #fbbf24; }
    .chat__msg--system .chat__msg-head { color: #fecaca; }
    .chat__msg-time {
      margin-left: auto;
      font-weight: 500;
      color: #64748b;
      letter-spacing: 0;
      text-transform: none;
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }

    .chat__msg-body {
      color: #e2e8f0;
      font-size: 13.5px;
      line-height: 1.6;
      word-break: break-word;
    }
    .chat__msg--user .chat__msg-body { color: #ccfbf1; white-space: pre-wrap; }
    .chat__msg--system .chat__msg-body { color: #fecaca; white-space: pre-wrap; }
    .chat__msg--agent .chat__msg-body { color: #ede9fe; }

    /* Markdown rendering for non-user roles, mirroring activity-log-view. */
    .chat__msg-body--markdown :first-child { margin-top: 0; }
    .chat__msg-body--markdown :last-child  { margin-bottom: 0; }
    .chat__msg-body--markdown p { margin: 0 0 0.8em; }
    .chat__msg-body--markdown h1,
    .chat__msg-body--markdown h2,
    .chat__msg-body--markdown h3 {
      margin: 0.8em 0 0.4em; color: #f5f3ff; font-weight: 700; line-height: 1.3;
    }
    .chat__msg-body--markdown h1 { font-size: 1.25em; }
    .chat__msg-body--markdown h2 { font-size: 1.15em; }
    .chat__msg-body--markdown h3 { font-size: 1.05em; }
    .chat__msg-body--markdown ul,
    .chat__msg-body--markdown ol { margin: 0 0 0.8em; padding-left: 1.4em; }
    .chat__msg-body--markdown li { margin: 0.2em 0; }
    .chat__msg-body--markdown strong { color: #fafaff; font-weight: 700; }
    .chat__msg-body--markdown em { color: #ddd6fe; }
    .chat__msg-body--markdown a { color: #a5b4fc; text-decoration: underline; text-underline-offset: 2px; }
    .chat__msg-body--markdown a:hover { color: #c7d2fe; }
    .chat__msg-body--markdown code {
      background: rgba(2,6,23,0.6);
      border: 1px solid rgba(148,163,184,0.18);
      border-radius: 4px;
      padding: 1px 5px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 0.92em;
      color: #fef3c7;
    }
    .chat__msg-body--markdown pre {
      margin: 0.6em 0;
      padding: 10px 12px;
      background: rgba(2,6,23,0.7);
      border: 1px solid rgba(148,163,184,0.16);
      border-radius: 8px;
      overflow: auto;
      max-height: 360px;
    }
    .chat__msg-body--markdown pre code {
      background: transparent; border: 0; padding: 0;
      font-size: 12px; color: #e0f2fe; white-space: pre;
    }

    .chat__msg-attachments {
      list-style: none;
      margin: 4px 0 0;
      padding: 0;
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .chat__msg-attachment {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      padding: 4px 6px 6px;
      background: rgba(0,0,0,0.25);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      max-width: 140px;
    }
    .chat__msg-attachment--pending { border-style: dashed; opacity: 0.7; }
    .chat__msg-attachment img {
      width: 120px;
      height: 80px;
      object-fit: cover;
      border-radius: 4px;
      border: 1px solid rgba(255,255,255,0.08);
    }
    .chat__msg-attachment-name {
      font-size: 11px;
      color: #94a3b8;
      max-width: 120px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chat__msg-error {
      margin-top: 4px;
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 8px;
    }

    .chat__typing {
      display: flex;
      gap: 4px;
      padding: 8px 14px;
      align-self: flex-start;
    }
    .chat__typing span {
      width: 6px;
      height: 6px;
      border-radius: 999px;
      background: rgba(196,181,253,0.55);
      animation: chat-typing 1.2s ease-in-out infinite;
    }
    .chat__typing span:nth-child(2) { animation-delay: 0.15s; }
    .chat__typing span:nth-child(3) { animation-delay: 0.3s; }
    @keyframes chat-typing {
      0%, 80%, 100% { opacity: 0.3; transform: translateY(0); }
      40% { opacity: 1; transform: translateY(-2px); }
    }

    .chat__composer {
      border-top: 1px solid rgba(255,255,255,0.06);
      padding: 10px 12px 12px;
      background: rgba(255,255,255,0.02);
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .chat__composer--drag {
      box-shadow: inset 0 0 0 2px rgba(56,189,248,0.55);
    }
    .chat__drafts {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .chat__draft {
      position: relative;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      padding: 4px 6px 6px;
      background: rgba(0,0,0,0.25);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      max-width: 120px;
    }
    .chat__draft img {
      width: 100px;
      height: 70px;
      object-fit: cover;
      border-radius: 4px;
      border: 1px solid rgba(255,255,255,0.08);
    }
    .chat__draft-name {
      font-size: 11px;
      color: #94a3b8;
      max-width: 100px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chat__draft-remove {
      position: absolute;
      top: 2px;
      right: 4px;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      border: 0;
      background: rgba(0,0,0,0.55);
      color: #f8fafc;
      font-size: 13px;
      line-height: 1;
      cursor: pointer;
    }
    .chat__draft-remove:hover { background: rgba(239,68,68,0.7); }
    .chat__attachment-error {
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 8px;
    }

    .chat__composer-row {
      display: flex;
      align-items: flex-end;
      gap: 8px;
    }
    .chat__input {
      flex: 1;
      min-height: 44px;
      max-height: 240px;
      resize: vertical;
      background: rgba(0,0,0,0.30);
      color: #e2e8f0;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      padding: 8px 10px;
      font-family: inherit;
      font-size: 13.5px;
      line-height: 1.5;
    }
    .chat__input:focus { outline: none; border-color: #6366f1; }
    .chat__input:disabled { opacity: 0.5; cursor: not-allowed; }

    .chat__composer-actions {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .chat__icon-btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #cbd5e1;
      width: 36px;
      height: 36px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 16px;
      line-height: 1;
    }
    .chat__icon-btn:hover:not(:disabled) {
      background: rgba(99,102,241,0.18);
      color: #ddd6fe;
      border-color: rgba(167,139,250,0.55);
    }
    .chat__icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }
    .chat__file-input { display: none; }
    .chat__send-btn {
      background: rgba(99,102,241,0.85);
      border: 1px solid rgba(165,180,252,0.85);
      color: #ffffff;
      padding: 8px 16px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 13px;
      font-weight: 600;
      min-width: 64px;
    }
    .chat__send-btn:hover:not(:disabled) {
      background: rgba(99,102,241,1);
      border-color: rgba(196,181,253,1);
    }
    .chat__send-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  `]
})
export class ChatComponent implements AfterViewInit, OnDestroy {
  readonly messages = input<ChatMessage[]>([]);
  readonly placeholder = input<string>('Type a message…');
  readonly emptyState = input<string>('No messages yet.');
  readonly submitLabel = input<string>('Send');
  readonly bodyMaxHeight = input<string>('100%');
  readonly disabled = input<boolean>(false);
  readonly pending = input<boolean>(false);
  readonly variant = input<'framed' | 'embedded'>('framed');
  readonly allowAttachments = input<boolean>(true);
  readonly maxAttachmentBytes = input<number>(10 * 1024 * 1024);

  readonly submitMessage = output<ChatSubmitEvent>();

  readonly drafts = signal<ChatDraftAttachment[]>([]);
  readonly attachmentError = signal<string | null>(null);
  readonly stickToBottom = signal(true);
  readonly isDragging = signal(false);

  draftText = '';

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly inputRef = viewChild<ElementRef<HTMLTextAreaElement>>('input');
  private readonly fileInputRef = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;

  private readonly sanitizer = inject(DomSanitizer);

  readonly rendered = computed<RenderedMessage[]>(() =>
    this.messages().map((message) => ({
      message,
      // User input stays plain text (newlines + escaping); every other role
      // ships Markdown, which is how agents and orchestrator log entries
      // express themselves on the wire.
      bodyHtml: this.sanitizer.bypassSecurityTrustHtml(
        message.role === 'user'
          ? escapeForPlain(message.text)
          : markdownToHtml(message.text)
      )
    }))
  );

  private readonly autoScrollEffect = effect(() => {
    this.messages();
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

function escapeForPlain(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\n/g, '<br>');
}
