import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ConceptKey, getConceptEntry } from '../../concept-docs/concept-doc-registry';
import { ModalStackService } from '../../services/modal-stack.service';
import { OverlayPortalDirective } from '../../directives/overlay-portal.directive';
import { ConnectedOverlayPositionRef, OverlayPortalService } from '../../services/overlay-portal.service';

import { TooltipDirective } from '../tooltip';
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
  imports: [TooltipDirective, OverlayPortalDirective],
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
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly overlayPortal = inject(OverlayPortalService);
  private positionRef: ConnectedOverlayPositionRef | null = null;

  readonly popoverPos = signal<{ left: number; top: number }>({ left: 0, top: 0 });

  toggle(event: Event): void {
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
    const pop = this.popover()?.nativeElement;
    if (pop && event.target instanceof Node && pop.contains(event.target)) return;
    this.close();
  }

  // Escape routes through ModalStack so a real modal above wins first.
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;
  private readonly stackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.modalStackDispose) {
      this.modalStackDispose = this.modalStack.push('concept-help', () => this.close());
      queueMicrotask(() => this.positionPopover());
    } else if (!isOpen && this.modalStackDispose) {
      this.releasePositioner();
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly stackTeardown = this.destroyRef.onDestroy(() => {
    this.releasePositioner();
    this.modalStackDispose?.();
  });

  private positionPopover(): void {
    if (!this.open()) return;
    const trigger = this.trigger()?.nativeElement;
    const popover = this.popover()?.nativeElement;
    if (!trigger || !popover) return;
    this.releasePositioner();
    this.positionRef = this.overlayPortal.watchConnectedPosition(trigger, popover, {
      preferredPlacement: 'below',
      alignment: 'start',
      gap: 8,
      viewportPadding: 8,
    }, pos => this.popoverPos.set({ left: pos.left, top: pos.top }));
  }

  private releasePositioner(): void {
    this.positionRef?.dispose();
    this.positionRef = null;
  }
}
