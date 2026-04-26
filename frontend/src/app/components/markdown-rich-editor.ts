import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { Editor } from '@tiptap/core';
import { htmlToMarkdown, markdownToHtml } from './markdown-utils';

@Component({
  selector: 'app-markdown-rich-editor',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="md-editor">
      <div class="md-editor__bar">
        <div class="md-editor__tabs">
          <button class="md-editor__tab"
                  [class.md-editor__tab--active]="mode() === 'rich'"
                  (click)="setMode('rich')">
            Rich text
          </button>
          <button class="md-editor__tab"
                  [class.md-editor__tab--active]="mode() === 'source'"
                  (click)="setMode('source')">
            Markdown
          </button>
        </div>
        <button class="md-editor__save" (click)="emitSave()">Save</button>
      </div>

      <div class="md-editor__content">
        <div #editorHost class="md-editor__rich" [class.md-editor__rich--hidden]="mode() !== 'rich'"></div>
        @if (mode() === 'source') {
          <textarea class="md-editor__source"
                    [ngModel]="sourceValue()"
                    (ngModelChange)="updateSource($event)"
                    (keydown)="handleKeydown($event)"></textarea>
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
    .md-editor {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-height: 0;
    }
    .md-editor__bar {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin-bottom: 6px;
    }
    .md-editor__tabs {
      display: inline-flex;
      gap: 4px;
      padding: 2px;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 6px;
      background: rgba(255,255,255,0.03);
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
  `]
})
export class MarkdownRichEditorComponent implements AfterViewInit, OnDestroy {
  readonly value = input('');
  readonly save = output<string>();
  readonly sourceValue = signal('');
  readonly mode = signal<'rich' | 'source'>('rich');

  @ViewChild('editorHost') private editorHost?: ElementRef<HTMLElement>;

  private editor: Editor | null = null;
  private destroyed = false;
  private readonly valueEffect = effect(() => {
    const next = this.value() ?? '';
    if (next === this.sourceValue()) return;
    this.sourceValue.set(next);
    if (this.editor) {
      this.editor.commands.setContent(markdownToHtml(next), { emitUpdate: false });
    }
  });

  ngAfterViewInit(): void {
    void this.initEditor();
  }

  private async initEditor(): Promise<void> {
    this.sourceValue.set(this.value() ?? '');
    const [{ Editor }, { default: StarterKit }] = await Promise.all([
      import('@tiptap/core'),
      import('@tiptap/starter-kit')
    ]);

    if (this.destroyed) return;

    this.editor = new Editor({
      element: this.editorHost?.nativeElement,
      extensions: [StarterKit],
      content: markdownToHtml(this.sourceValue()),
      editorProps: {
        handleKeyDown: (_view, event) => {
          if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault();
            this.emitSave();
            return true;
          }
          return false;
        }
      },
      onUpdate: ({ editor }) => {
        this.sourceValue.set(htmlToMarkdown(editor.getHTML()));
      }
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.valueEffect.destroy();
    this.editor?.destroy();
  }

  setMode(mode: 'rich' | 'source'): void {
    this.mode.set(mode);
  }

  updateSource(value: string): void {
    this.sourceValue.set(value);
    this.editor?.commands.setContent(markdownToHtml(value), { emitUpdate: false });
  }

  handleKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.emitSave();
    }
  }

  emitSave(): void {
    this.save.emit(this.sourceValue());
  }
}
