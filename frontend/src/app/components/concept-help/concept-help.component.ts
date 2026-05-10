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
  templateUrl: './concept-help.component.html',
  styleUrl: './concept-help.component.scss'
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
