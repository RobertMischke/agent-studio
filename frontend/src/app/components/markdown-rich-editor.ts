import { AfterViewInit, Component, ElementRef, HostListener, OnDestroy, ViewChild, computed, effect, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { Editor } from '@tiptap/core';
import { htmlToMarkdown, markdownToHtml, MarkdownImageOptions } from './markdown-utils';
import { shouldEmitEditorSave } from './markdown-rich-editor.guard';

type EditorState = 'idle' | 'dirty' | 'saved';

const ATTACHMENTS_PREFIX = 'attachments/';

@Component({
  selector: 'app-markdown-rich-editor',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="md-editor"
         [class.md-editor--dirty]="state() === 'dirty'"
         [class.md-editor--saved]="state() === 'saved'"
         [class.md-editor--readonly]="readOnly()"
         [class.md-editor--drag]="isDragging()"
         [attr.data-state]="state()"
         data-testid="prompt-editor"
         (dragover)="onDragOver($event)"
         (dragleave)="onDragLeave($event)"
         (drop)="onDrop($event)">
      @if (readOnly()) {
        <div class="md-editor__lock" data-testid="prompt-editor-lock">
          🔒 {{ readOnlyReason() || 'Editing disabled while the CLI is running for this task. Stop it first.' }}
        </div>
      }
      <!--
        Chat-first redesign: the toolbar is a tight icon strip; tooltips
        carry the labels the verbose row used to render in visible text.
        Save state is a small coloured dot rather than a long pill. -->
      <div class="md-editor__bar md-editor__bar--compact">
        <div class="md-editor__tabs">
          <button class="md-editor__tab md-editor__tab--icon"
                  [class.md-editor__tab--active]="mode() === 'rich'"
                  (click)="setMode('rich')"
                  aria-label="Rich text editor"
                  title="Rich text editor">P</button>
          <button class="md-editor__tab md-editor__tab--icon"
                  [class.md-editor__tab--active]="mode() === 'source'"
                  (click)="setMode('source')"
                  aria-label="Markdown source editor"
                  title="Markdown source editor">M</button>
          @if (canAttach()) {
            <button class="md-editor__tab md-editor__tab--icon md-editor__tab--upload"
                    type="button"
                    [disabled]="readOnly() || uploading()"
                    (click)="triggerFilePicker()"
                    data-testid="prompt-editor-attach"
                    [attr.aria-label]="uploading() ? 'Uploading image' : 'Insert image'"
                    [title]="uploading() ? 'Uploading…' : 'Insert image (Ctrl+V also works)'">
              {{ uploading() ? '⏳' : '📎' }}
            </button>
          }
        </div>
        <div class="md-editor__status" data-testid="prompt-editor-status">
          @switch (state()) {
            @case ('dirty') {
              <span class="md-editor__dot md-editor__dot--dirty" title="Unsaved changes" aria-label="Unsaved changes"></span>
            }
            @case ('saved') {
              <span class="md-editor__dot md-editor__dot--saved" title="Saved" aria-label="Saved"></span>
            }
            @default {
              <span class="md-editor__dot" title="Saved" aria-label="Saved"></span>
            }
          }
          <button class="md-editor__save md-editor__save--icon"
                  (click)="emitSave()"
                  [disabled]="readOnly()"
                  data-testid="prompt-editor-save"
                  aria-label="Save (Ctrl+S)"
                  title="Save (Ctrl+S)">💾</button>
        </div>
      </div>

      <div class="md-editor__content">
        <div #editorHost class="md-editor__rich" [class.md-editor__rich--hidden]="mode() !== 'rich'"></div>
        @if (mode() === 'source') {
          <textarea class="md-editor__source"
                    data-testid="prompt-editor-source"
                    [ngModel]="sourceValue()"
                    (ngModelChange)="updateSource($event)"
                    (keydown)="handleKeydown($event)"
                    (paste)="handleSourcePaste($event)"
                    [readonly]="readOnly()"></textarea>
        }
      </div>

      @if (uploadError()) {
        <div class="md-editor__error" data-testid="prompt-editor-upload-error">
          ⚠ {{ uploadError() }}
        </div>
      }

      <input #fileInput
             type="file"
             accept="image/png,image/jpeg,image/gif,image/webp"
             class="md-editor__file"
             (change)="onFileInputChange($event)" />
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      min-height: 0;
      flex: 1;
    }
    .md-editor {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-height: 0;
      position: relative;
    }
    .md-editor__bar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin-bottom: 6px;
    }
    .md-editor__bar--compact {
      min-height: 24px;
      margin-bottom: 4px;
    }
    .md-editor__tabs {
      display: inline-flex;
      gap: 2px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 3px;
      background: rgba(255,255,255,0.03);
    }
    .md-editor__tab--icon {
      width: 22px;
      height: 22px;
      padding: 0;
      display: inline-grid;
      place-items: center;
      font-size: 12px;
      line-height: 1;
      border-radius: 2px;
    }
    .md-editor__dot {
      width: 8px;
      height: 8px;
      display: inline-block;
      border-radius: 50%;
      background: rgba(148,163,184,0.45);
      cursor: help;
    }
    .md-editor__dot--dirty { background: #c4b5fd; box-shadow: 0 0 0 2px rgba(139,92,246,0.25); }
    .md-editor__dot--saved { background: #86efac; box-shadow: 0 0 0 2px rgba(34,197,94,0.25); }
    .md-editor__save--icon {
      width: 24px;
      height: 22px;
      padding: 0;
      display: inline-grid;
      place-items: center;
      font-size: 12px;
      border-radius: 3px;
    }
    .md-editor__status {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .md-editor__status-pill {
      font-size: 11px;
      letter-spacing: 0.02em;
      color: #94a3b8;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid rgba(255,255,255,0.08);
      background: rgba(255,255,255,0.03);
      transition: color 200ms ease, background 200ms ease, border-color 200ms ease;
    }
    .md-editor__status-pill--dirty {
      color: #ddd6fe;
      border-color: rgba(139,92,246,0.45);
      background: rgba(139,92,246,0.18);
    }
    .md-editor__status-pill--saved {
      color: #86efac;
      border-color: rgba(34,197,94,0.55);
      background: rgba(34,197,94,0.22);
    }
    .md-editor__tab,
    .md-editor__save {
      border: 0;
      border-radius: 4px;
      color: #94a3b8;
      background: transparent;
      cursor: pointer;
      font-size: 12px;
      padding: 4px 8px;
    }
    .md-editor__tab--active {
      color: #ddd6fe;
      background: rgba(99,102,241,0.25);
    }
    .md-editor__tab--upload {
      color: #cbd5e1;
      border-left: 1px solid rgba(255,255,255,0.08);
      margin-left: 4px;
      padding-left: 10px;
    }
    .md-editor__tab--upload:hover:not(:disabled) {
      color: #ddd6fe;
      background: rgba(99,102,241,0.18);
    }
    .md-editor__tab--upload:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
    .md-editor__save {
      border: 1px solid rgba(34,197,94,0.35);
      color: #86efac;
      background: rgba(34,197,94,0.12);
    }
    .md-editor__content {
      display: flex;
      flex: 1;
      min-height: 0;
    }
    .md-editor__rich,
    .md-editor__source {
      width: 100%;
      min-height: 150px;
      box-sizing: border-box;
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 10px;
      background: rgba(0,0,0,0.18);
      color: #cbd5e1;
      font: 14px/1.65 var(--font-mono, 'Consolas', monospace);
      overflow: auto;
      transition: border-color 200ms ease, box-shadow 200ms ease;
    }
    .md-editor--dirty .md-editor__rich,
    .md-editor--dirty .md-editor__source {
      border-color: rgba(139,92,246,0.55);
      box-shadow: 0 0 0 1px rgba(139,92,246,0.25);
    }
    .md-editor--saved .md-editor__rich,
    .md-editor--saved .md-editor__source {
      border-color: rgba(34,197,94,0.65);
      box-shadow: 0 0 0 1px rgba(34,197,94,0.30);
    }
    .md-editor--drag .md-editor__rich,
    .md-editor--drag .md-editor__source {
      border-color: rgba(56,189,248,0.85);
      box-shadow: 0 0 0 2px rgba(56,189,248,0.35);
    }
    .md-editor__rich {
      padding: 12px 14px;
    }
    .md-editor__rich--hidden {
      display: none;
    }
    .md-editor__source {
      flex: 1;
      padding: 12px 14px;
      resize: none;
    }
    .md-editor__lock {
      font-size: 12px;
      color: #fbbf24;
      background: rgba(251,191,36,0.10);
      border: 1px solid rgba(251,191,36,0.35);
      border-radius: 6px;
      padding: 6px 10px;
      margin-bottom: 6px;
    }
    .md-editor__error {
      margin-top: 6px;
      font-size: 12px;
      color: #fca5a5;
      background: rgba(239,68,68,0.10);
      border: 1px solid rgba(239,68,68,0.35);
      border-radius: 6px;
      padding: 6px 10px;
    }
    .md-editor__file {
      display: none;
    }
    .md-editor--readonly .md-editor__rich,
    .md-editor--readonly .md-editor__source {
      opacity: 0.7;
      cursor: not-allowed;
    }
    .md-editor--readonly .md-editor__save {
      opacity: 0.5;
      cursor: not-allowed;
    }
    .md-editor__source:focus,
    :host ::ng-deep .ProseMirror:focus {
      outline: none;
    }
    :host ::ng-deep .ProseMirror {
      min-height: 124px;
      white-space: pre-wrap;
    }
    :host ::ng-deep .ProseMirror p {
      margin: 0 0 8px;
    }
    :host ::ng-deep .ProseMirror h1,
    :host ::ng-deep .ProseMirror h2,
    :host ::ng-deep .ProseMirror h3 {
      margin: 0 0 8px;
      color: #f8fafc;
      line-height: 1.25;
    }
    :host ::ng-deep .ProseMirror ul {
      margin: 0 0 8px;
      padding-left: 18px;
    }
    :host ::ng-deep .ProseMirror code {
      color: #c4b5fd;
      background: rgba(124,58,237,0.16);
      border-radius: 4px;
      padding: 1px 4px;
    }
    :host ::ng-deep .ProseMirror img {
      max-width: 100%;
      max-height: 360px;
      display: block;
      margin: 6px 0;
      border-radius: 8px;
      border: 1px solid rgba(255,255,255,0.08);
    }
    :host ::ng-deep .ProseMirror img.ProseMirror-selectednode {
      outline: 2px solid #818cf8;
      outline-offset: 2px;
    }
  `]
})
export class MarkdownRichEditorComponent implements AfterViewInit, OnDestroy {
  readonly value = input('');
  readonly readOnly = input(false);
  readonly readOnlyReason = input<string | null>(null);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly save = output<string>();
  readonly sourceValue = signal('');
  readonly mode = signal<'rich' | 'source'>('rich');
  readonly savedAt = signal(0);
  readonly uploading = signal(false);
  readonly uploadError = signal<string | null>(null);
  readonly isDragging = signal(false);

  // Last value the parent told us about — anything diverging from this is "dirty".
  private readonly committedValue = signal('');

  readonly canAttach = computed(() => !!this.jobId());

  readonly state = computed<EditorState>(() => {
    if (this.savedAt() > 0) return 'saved';
    return this.sourceValue() !== this.committedValue() ? 'dirty' : 'idle';
  });

  @ViewChild('editorHost') private editorHost?: ElementRef<HTMLElement>;
  @ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;

  private editor: Editor | null = null;
  private destroyed = false;
  private savedTimer: ReturnType<typeof setTimeout> | null = null;
  private autosaveTimer: ReturnType<typeof setTimeout> | null = null;
  private dragCounter = 0;
  /**
   * True only after a real user edit has touched the editor. Tiptap's
   * onUpdate fires for both user input and (in some configurations) the
   * initial constructor content load, which used to race against the
   * parent's async detail() fetch: an autosave fired with the stub empty
   * value before the real prompt arrived, clobbering prompt.md on disk.
   * We gate scheduleAutosave on this flag and flip it only inside an
   * onUpdate that observes content actually different from
   * <see cref="committedValue"/>. Programmatic setContent calls in
   * <see cref="valueEffect"/> use `emitUpdate: false` so they never flip it.
   */
  private hasUserEdit = false;
  // Sync the parent-provided value into the editor whenever it changes. We
  // read sourceValue/committedValue via untracked() so user edits (which write
  // sourceValue) don't re-trigger this effect and revert the user's typing.
  private readonly valueEffect = effect(() => {
    const next = this.value() ?? '';
    untracked(() => {
      const prevCommitted = this.committedValue();
      this.committedValue.set(next);
      if (next === this.sourceValue()) return;
      // Skip reset when the server echoes back the value we just autosaved
      // while the user has already typed more content. Without this guard an
      // autosave round-trip (save → re-fetch → valueEffect) would overwrite
      // in-progress typing with the slightly-stale saved value.
      if (next === prevCommitted && this.sourceValue() !== prevCommitted) return;
      this.sourceValue.set(next);
      if (this.editor) {
        this.editor.commands.setContent(this.toHtml(next), { emitUpdate: false });
      }
    });
  });

  private readonly readOnlyEffect = effect(() => {
    const locked = this.readOnly();
    untracked(() => {
      this.editor?.setEditable(!locked);
    });
  });

  ngAfterViewInit(): void {
    void this.initEditor();
  }

  private async initEditor(): Promise<void> {
    this.sourceValue.set(this.value() ?? '');
    const [{ Editor }, { default: StarterKit }, imageMod] = await Promise.all([
      import('@tiptap/core'),
      import('@tiptap/starter-kit'),
      import('@tiptap/extension-image')
    ]);

    if (this.destroyed) return;

    const Image = imageMod.default ?? imageMod.Image;

    this.editor = new Editor({
      element: this.editorHost?.nativeElement,
      extensions: [
        StarterKit,
        Image.configure({ inline: false, allowBase64: false })
      ],
      content: this.toHtml(this.sourceValue()),
      editable: !this.readOnly(),
      editorProps: {
        handleKeyDown: (_view, event) => {
          if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault();
            this.emitSave();
            return true;
          }
          return false;
        },
        handlePaste: (_view, event) => {
          const file = this.imageFromClipboard(event.clipboardData);
          if (!file) return false;
          event.preventDefault();
          void this.uploadAndInsert(file);
          return true;
        },
        handleDrop: (_view, event, _slice, moved) => {
          if (moved) return false;
          const dt = (event as DragEvent).dataTransfer;
          const file = this.imageFromDataTransfer(dt);
          if (!file) return false;
          event.preventDefault();
          void this.uploadAndInsert(file);
          return true;
        }
      },
      onUpdate: ({ editor }) => {
        const next = htmlToMarkdown(editor.getHTML(), this.markdownOptions());
        this.sourceValue.set(next);
        // Only count this as a user edit (and therefore arm autosave) when
        // the new content actually diverges from what the parent told us.
        // Tiptap fires onUpdate for the constructor's content load on some
        // versions; without this guard a stub empty mount would autosave ''
        // before the real prompt arrives and clobber prompt.md on disk.
        if (next !== this.committedValue()) {
          this.hasUserEdit = true;
          this.scheduleAutosave();
        }
      }
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.valueEffect.destroy();
    this.readOnlyEffect.destroy();
    this.editor?.destroy();
    if (this.savedTimer) clearTimeout(this.savedTimer);
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
  }

  setMode(mode: 'rich' | 'source'): void {
    this.mode.set(mode);
  }

  updateSource(value: string): void {
    this.sourceValue.set(value);
    this.editor?.commands.setContent(this.toHtml(value), { emitUpdate: false });
    // Source-mode edit is unambiguously user-driven (the textarea binds to
    // ngModelChange); flag hasUserEdit so the no-clobber rule lets the
    // resulting autosave through.
    if (value !== this.committedValue()) this.hasUserEdit = true;
    this.scheduleAutosave();
  }

  private scheduleAutosave(): void {
    if (this.readOnly()) return;
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
    this.autosaveTimer = setTimeout(() => {
      this.autosaveTimer = null;
      this.emitSave();
    }, 600);
  }

  handleKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.emitSave();
    }
  }

  // Catch Ctrl+S anywhere on the page while the editor is mounted, so users
  // don't have to focus the editor first. The detail view hosts a single
  // prompt editor at a time, so a window-level listener is unambiguous.
  @HostListener('window:keydown', ['$event'])
  onWindowKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.emitSave();
    }
  }

  emitSave(): void {
    if (this.readOnly()) return;
    const value = this.sourceValue();
    // No-clobber rule: don't persist the editor value unless the user
    // actually touched it AND it diverges from what the parent committed.
    // Without this guard, the empty initial-mount race overwrites
    // prompt.md before the parent's async detail() fetch arrives. See
    // shouldEmitEditorSave for the full rationale.
    if (!shouldEmitEditorSave({
      current: value,
      committed: this.committedValue(),
      hasUserEdit: this.hasUserEdit
    })) return;
    this.committedValue.set(value);
    // After a real save, clear the user-edit flag so a fresh remount-with-
    // stub-value race cannot reuse the latched true to push another empty.
    // The next user keystroke will set it again.
    this.hasUserEdit = false;
    this.save.emit(value);
    if (this.savedTimer) clearTimeout(this.savedTimer);
    this.savedAt.set(Date.now());
    this.savedTimer = setTimeout(() => {
      this.savedAt.set(0);
      this.savedTimer = null;
    }, 1400);
  }

  triggerFilePicker(): void {
    if (this.readOnly() || this.uploading()) return;
    this.fileInput?.nativeElement.click();
  }

  onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) void this.uploadAndInsert(file);
    input.value = '';
  }

  handleSourcePaste(event: ClipboardEvent): void {
    const file = this.imageFromClipboard(event.clipboardData);
    if (!file) return;
    event.preventDefault();
    void this.uploadAndInsert(file);
  }

  onDragOver(event: DragEvent): void {
    if (!this.canAttach() || this.readOnly()) return;
    if (!event.dataTransfer) return;
    if (!Array.from(event.dataTransfer.types).includes('Files')) return;
    event.preventDefault();
    this.dragCounter = Math.max(this.dragCounter, 1);
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent): void {
    if (!this.isDragging()) return;
    // Only reset when the cursor actually leaves the host, not just moves
    // between child elements.
    if (event.target !== event.currentTarget) return;
    this.isDragging.set(false);
    this.dragCounter = 0;
  }

  onDrop(event: DragEvent): void {
    this.isDragging.set(false);
    this.dragCounter = 0;
    if (!this.canAttach() || this.readOnly()) return;
    const file = this.imageFromDataTransfer(event.dataTransfer);
    if (!file) return;
    event.preventDefault();
    // Source-mode drop: insert an attachments/<file> reference at the end.
    if (this.mode() === 'source') {
      void this.uploadAndAppendSource(file);
    } else {
      void this.uploadAndInsert(file);
    }
  }

  private imageFromClipboard(data: DataTransfer | null): File | null {
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

  private imageFromDataTransfer(data: DataTransfer | null): File | null {
    if (!data) return null;
    for (const file of Array.from(data.files ?? [])) {
      if (file.type.startsWith('image/')) return file;
    }
    return null;
  }

  private async uploadAndInsert(file: File): Promise<void> {
    const upload = await this.uploadAttachment(file);
    if (!upload) return;
    if (this.editor) {
      this.editor
        .chain()
        .focus()
        .setImage({ src: upload.absoluteUrl, alt: upload.alt })
        .run();
    } else {
      // Editor not yet ready — append directly to the source instead.
      await this.uploadAndAppendSource(file, upload);
    }
  }

  private async uploadAndAppendSource(file: File, upload?: UploadResult | null): Promise<void> {
    const result = upload ?? (await this.uploadAttachment(file));
    if (!result) return;
    const ref = `\n\n![${result.alt}](${result.relativePath})\n`;
    this.updateSource((this.sourceValue() ?? '') + ref);
  }

  private async uploadAttachment(file: File): Promise<UploadResult | null> {
    const jobId = this.jobId();
    if (!jobId) {
      this.uploadError.set('Cannot attach images on this editor instance.');
      return null;
    }
    if (this.readOnly()) return null;
    if (file.size > 10 * 1024 * 1024) {
      this.uploadError.set('Image too large (max 10 MB).');
      return null;
    }

    this.uploadError.set(null);
    this.uploading.set(true);
    try {
      const watchPath = this.watchPath();
      const url = `/api/jobs/${encodeURIComponent(jobId)}/attachments`
        + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
      const form = new FormData();
      form.append('file', file, file.name || 'pasted-image.png');
      const res = await fetch(url, { method: 'POST', body: form });
      if (!res.ok) {
        const body = await res.text().catch(() => '');
        this.uploadError.set(`Upload failed (${res.status}): ${body || res.statusText}`);
        return null;
      }
      const payload = (await res.json()) as { fileName: string; relativePath: string; url: string };
      return {
        fileName: payload.fileName,
        relativePath: payload.relativePath,
        absoluteUrl: this.absoluteAttachmentUrl(payload.relativePath),
        alt: this.deriveAlt(file)
      };
    } catch (err) {
      this.uploadError.set(`Upload failed: ${(err as Error).message ?? err}`);
      return null;
    } finally {
      this.uploading.set(false);
    }
  }

  private deriveAlt(file: File): string {
    const stem = (file.name ?? '').replace(/\.[^.]+$/, '').trim();
    return stem || 'screenshot';
  }

  private absoluteAttachmentUrl(relativePath: string): string {
    const jobId = this.jobId();
    if (!jobId) return relativePath;
    const watchPath = this.watchPath();
    const fileName = relativePath.startsWith(ATTACHMENTS_PREFIX)
      ? relativePath.slice(ATTACHMENTS_PREFIX.length)
      : relativePath;
    const qs = watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '';
    return `/api/jobs/${encodeURIComponent(jobId)}/attachments/${encodeURIComponent(fileName)}${qs}`;
  }

  private markdownOptions(): MarkdownImageOptions {
    return {
      resolveImageSrc: (src) => {
        if (!src.startsWith(ATTACHMENTS_PREFIX)) return src;
        return this.absoluteAttachmentUrl(src);
      },
      serializeImageSrc: (src) => {
        // Pull `attachments/<file>` back out of an absolute API URL so prompt.md
        // on disk keeps the same relative reference the agent can resolve.
        const match = /\/attachments\/([^/?#]+)/.exec(src);
        if (match) return `${ATTACHMENTS_PREFIX}${decodeURIComponent(match[1])}`;
        return src;
      }
    };
  }

  private toHtml(markdown: string): string {
    return markdownToHtml(markdown, this.markdownOptions());
  }
}

interface UploadResult {
  fileName: string;
  relativePath: string;
  absoluteUrl: string;
  alt: string;
}
