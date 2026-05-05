import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ConceptKey, getConceptEntry } from '../../concept-docs/concept-doc-registry';

const REPO_BLOB_BASE = 'https://github.com/RobertMischke/agent-taskboard/blob/main/';

/**
 * Tiny "i" trigger that opens an in-product concept popover next to a panel
 * title. Keeps users from leaving the app to read the rationale: the
 * popover renders a short paragraph plus a "Learn more" link to the
 * matching doc under docs/.
 *
 * One canonical concept entry per concept (see ../../concept-docs/). Wire
 * the same concept key wherever the concept appears in the UI rather than
 * paraphrasing it twice.
 */
@Component({
  selector: 'app-concept-help',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="concept-help" [attr.data-concept]="concept()">
      <button
        type="button"
        class="concept-help__trigger"
        [class.concept-help__trigger--open]="open()"
        [attr.data-testid]="'concept-help-trigger-' + concept()"
        [attr.aria-expanded]="open()"
        [attr.aria-label]="'About ' + entry().title"
        title="About this concept"
        (click)="toggle($event)">
        i
      </button>
      @if (open()) {
        <div
          #popover
          class="concept-help__popover"
          role="dialog"
          [attr.aria-label]="entry().title"
          [attr.data-testid]="'concept-help-popover-' + concept()">
          <header class="concept-help__head">
            <h4 class="concept-help__title" [attr.data-testid]="'concept-help-title-' + concept()">
              {{ entry().title }}
            </h4>
            <button
              type="button"
              class="concept-help__close"
              [attr.data-testid]="'concept-help-close-' + concept()"
              aria-label="Close"
              (click)="close()">×</button>
          </header>
          <div class="concept-help__body" [attr.data-testid]="'concept-help-body-' + concept()">
            @for (p of paragraphs(); track $index) {
              <p>{{ p }}</p>
            }
          </div>
          <footer class="concept-help__foot">
            <a
              class="concept-help__learn"
              [attr.data-testid]="'concept-help-learn-' + concept()"
              [attr.href]="learnHref()"
              target="_blank"
              rel="noopener noreferrer">
              Learn more: {{ entry().learnMoreLabel }} →
            </a>
            <span class="concept-help__path" [attr.data-testid]="'concept-help-path-' + concept()">
              {{ entry().learnMore }}
            </span>
          </footer>
        </div>
      }
    </span>
  `,
  styles: [`
    :host { display: inline-flex; align-items: center; }

    .concept-help { position: relative; display: inline-flex; align-items: center; margin-left: 6px; }

    .concept-help__trigger {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 18px;
      height: 18px;
      border-radius: 50%;
      border: 1px solid rgba(148, 163, 184, 0.45);
      background: transparent;
      color: rgba(226, 232, 240, 0.78);
      font-family: Georgia, 'Times New Roman', serif;
      font-style: italic;
      font-size: 11px;
      line-height: 1;
      padding: 0;
      cursor: pointer;
      transition: background 0.12s ease, border-color 0.12s ease, color 0.12s ease;
    }
    .concept-help__trigger:hover,
    .concept-help__trigger:focus-visible {
      border-color: rgba(203, 166, 247, 0.65);
      background: rgba(203, 166, 247, 0.18);
      color: #e9d5ff;
      outline: none;
    }
    .concept-help__trigger--open {
      border-color: rgba(203, 166, 247, 0.85);
      background: rgba(203, 166, 247, 0.22);
      color: #f1f5f9;
    }

    .concept-help__popover {
      position: absolute;
      top: calc(100% + 8px);
      left: 0;
      z-index: 9999;
      min-width: 260px;
      max-width: 360px;
      padding: 12px 14px;
      background: #0b1020;
      color: #e2e8f0;
      border: 1px solid rgba(148, 163, 184, 0.30);
      border-radius: 8px;
      box-shadow: 0 12px 28px rgba(0, 0, 0, 0.55);
      font-size: 0.82rem;
      line-height: 1.5;
    }

    .concept-help__head {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 12px;
      margin-bottom: 6px;
    }
    .concept-help__title {
      margin: 0;
      font-size: 0.92rem;
      font-weight: 600;
      color: #f1f5f9;
    }
    .concept-help__close {
      background: transparent;
      border: none;
      color: rgba(226, 232, 240, 0.65);
      font-size: 1.05rem;
      line-height: 1;
      padding: 0 2px;
      cursor: pointer;
    }
    .concept-help__close:hover { color: #f1f5f9; }

    .concept-help__body p {
      margin: 0 0 8px;
      color: #e2e8f0;
    }
    .concept-help__body p:last-child { margin-bottom: 0; }

    .concept-help__foot {
      margin-top: 10px;
      padding-top: 8px;
      border-top: 1px solid rgba(148, 163, 184, 0.18);
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .concept-help__learn {
      color: #a5b4fc;
      text-decoration: none;
      font-weight: 600;
    }
    .concept-help__learn:hover { color: #c4b5fd; text-decoration: underline; }
    .concept-help__path {
      color: rgba(148, 163, 184, 0.75);
      font-family: ui-monospace, monospace;
      font-size: 0.72rem;
    }
  `]
})
export class ConceptHelpComponent {
  readonly concept = input.required<ConceptKey>();

  readonly open = signal(false);
  readonly entry = computed(() => getConceptEntry(this.concept()));
  readonly paragraphs = computed(() =>
    this.entry().body.split(/\n\s*\n/).map(p => p.trim()).filter(p => p.length > 0)
  );
  readonly learnHref = computed(() => REPO_BLOB_BASE + this.entry().learnMore);

  private readonly popover = viewChild<ElementRef<HTMLElement>>('popover');
  private readonly host = inject(ElementRef<HTMLElement>);

  toggle(event: MouseEvent): void {
    event.stopPropagation();
    this.open.update(v => !v);
  }

  close(): void {
    this.open.set(false);
  }

  @HostListener('document:click', ['$event'])
  onDocClick(event: MouseEvent): void {
    if (!this.open()) return;
    const root = this.host.nativeElement as HTMLElement;
    if (root && event.target instanceof Node && root.contains(event.target)) return;
    this.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }
}
