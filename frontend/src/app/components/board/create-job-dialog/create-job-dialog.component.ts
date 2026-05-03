import { ChangeDetectionStrategy, Component, ElementRef, HostListener, ViewChild, computed, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CliModelInfo, CliType, CLI_TYPES, WatchPathEntry } from '../../../models/job.model';
import { cliTypeIcon as fmtCliTypeIcon, cliTypeLabel as fmtCliTypeLabel, formatMultiplier as fmtMultiplier } from '../../../services/format.util';

export interface PendingAttachment {
  id: string;
  file: File;
  alt: string;
  previewUrl: string;
}

const PENDING_PREFIX = 'pending-attachment-';

/**
 * "Create task" dialog. The parent owns all draft signals and the
 * model catalog; this component renders the form, captures pasted/
 * dropped images as `PendingAttachment`s (the actual upload happens
 * after the job folder is created), and emits intent (cancel /
 * submit / cliType change). Two-way bindings via `model()` keep
 * title / watchPath / model / prompt / attachments in sync with the
 * parent.
 */
@Component({
  selector: 'app-create-job-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './create-job-dialog.component.html'
})
export class CreateJobDialogComponent {
  readonly title = input<string>('');
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly availableModels = input<CliModelInfo[]>([]);
  readonly cliTypeDraft = input.required<CliType>();

  readonly newTitle = model<string>('');
  readonly newWatchPath = model<string>('');
  readonly newModel = model<string>('');
  readonly newPrompt = model<string>('');
  readonly attachments = model<PendingAttachment[]>([]);

  readonly cliTypeChange = output<CliType>();
  readonly cancel = output<void>();
  readonly submit = output<void>();

  readonly isDragging = model<boolean>(false);
  readonly attachmentError = model<string | null>(null);

  readonly cliTypes = CLI_TYPES;
  cliTypeLabel(t: CliType): string { return fmtCliTypeLabel(t); }
  cliTypeIcon(t: CliType): string { return fmtCliTypeIcon(t); }
  formatMultiplier(mult: number | null): string { return fmtMultiplier(mult); }

  readonly hasAttachments = computed(() => this.attachments().length > 0);

  @ViewChild('promptArea') private promptArea?: ElementRef<HTMLTextAreaElement>;
  @ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;

  triggerFilePicker(): void {
    this.fileInput?.nativeElement.click();
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') return;
    event.preventDefault();
    event.stopPropagation();
    this.cancel.emit();
  }

  onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    for (const file of files) {
      if (file.type.startsWith('image/')) this.addAttachment(file);
    }
    input.value = '';
  }

  onPromptPaste(event: ClipboardEvent): void {
    const file = this.imageFromClipboard(event.clipboardData);
    if (!file) return;
    event.preventDefault();
    this.addAttachment(file);
  }

  onDragOver(event: DragEvent): void {
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
    const files = Array.from(event.dataTransfer?.files ?? []).filter(f => f.type.startsWith('image/'));
    if (files.length === 0) return;
    event.preventDefault();
    for (const file of files) this.addAttachment(file);
  }

  removeAttachment(id: string): void {
    const list = this.attachments();
    const found = list.find(a => a.id === id);
    if (found) URL.revokeObjectURL(found.previewUrl);
    this.attachments.set(list.filter(a => a.id !== id));

    // Drop the placeholder line out of the prompt as well.
    const current = this.newPrompt() ?? '';
    const stripped = current.replace(
      new RegExp(`!\\[[^\\]]*\\]\\(${PENDING_PREFIX}${id}\\)\\n?`, 'g'),
      ''
    );
    if (stripped !== current) this.newPrompt.set(stripped);
  }

  private addAttachment(file: File): void {
    if (file.size > 10 * 1024 * 1024) {
      this.attachmentError.set('Image too large (max 10 MB).');
      return;
    }
    this.attachmentError.set(null);

    const id = this.makeId();
    const alt = this.deriveAlt(file);
    const previewUrl = URL.createObjectURL(file);
    const next: PendingAttachment = { id, file, alt, previewUrl };
    this.attachments.set([...this.attachments(), next]);

    this.insertPlaceholder(alt, id);
  }

  private insertPlaceholder(alt: string, id: string): void {
    const ref = `![${alt}](${PENDING_PREFIX}${id})`;
    const area = this.promptArea?.nativeElement;
    const current = this.newPrompt() ?? '';

    if (area && document.activeElement === area) {
      const start = area.selectionStart ?? current.length;
      const end = area.selectionEnd ?? current.length;
      const before = current.slice(0, start);
      const after = current.slice(end);
      const needsLeadingNl = before.length > 0 && !before.endsWith('\n') ? '\n' : '';
      const insert = `${needsLeadingNl}${ref}\n`;
      const next = before + insert + after;
      this.newPrompt.set(next);
      // Move caret after the inserted reference on the next tick.
      queueMicrotask(() => {
        const pos = (before + insert).length;
        area.setSelectionRange(pos, pos);
        area.focus();
      });
    } else {
      const sep = current.length === 0 || current.endsWith('\n') ? '' : '\n';
      this.newPrompt.set(current + sep + ref + '\n');
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

  private deriveAlt(file: File): string {
    const stem = (file.name ?? '').replace(/\.[^.]+$/, '').trim();
    return stem || 'screenshot';
  }

  private makeId(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
    }
    return Math.random().toString(36).slice(2, 14);
  }
}
