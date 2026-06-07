import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { MarkdownViewComponent } from '../../../../../components/markdown-view/markdown-view.component';
import { TooltipDirective } from '../../../../../components/tooltip';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { ConnectedOverlayPositionRef, OverlayPortalRef, OverlayPortalService } from '../../../../../services/overlay-portal.service';
import { resolveProtocolImageSrc } from '../../protocol-pane/protocol-image-resolver';

/**
 * "Prompt" affordance for the Overview tab. A small trigger next to the task
 * title opens an anchored, read-only popover that renders the task prompt
 * (`prompt.md` / `promptMarkdown`) as Markdown via the shared
 * {@link MarkdownViewComponent}. The Overview tab summarises status, agent,
 * tokens and pipeline but never the prompt itself; this is the in-place way to
 * read what the task was actually asked to do without leaving for the Files tab.
 *
 * The popover panel is portaled to the central body overlay layer and anchored
 * back to the trigger. That keeps it above sibling panes and out of ancestor
 * overflow clipping. Closes on click-outside and on Escape
 * (routed through {@link ModalStackService} so a real modal above wins first,
 * and so Escape does not bubble out and close the whole detail panel). The body
 * is scrollable for long prompts. The trigger hides itself when there is no
 * prompt text so it never opens an empty popover.
 */
@Component({
  selector: 'app-task-prompt-popover',
  standalone: true,
  imports: [MarkdownViewComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-prompt-popover.component.html',
  styleUrl: './task-prompt-popover.component.scss',
})
export class TaskPromptPopoverComponent {
  /** Raw prompt markdown (`promptMarkdown` of the task). */
  readonly markdown = input<string | null | undefined>('');
  /** Job id + watch path, used to resolve `attachments/...` image refs. */
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly open = signal(false);

  /** True only when there is non-empty prompt text to show. */
  readonly hasPrompt = computed(() => (this.markdown() ?? '').trim().length > 0);

  /** Rewrites `attachments/foo.png` refs to the job-folder API URL so prompt
   *  images render inside the popover the same way they do on the Files tab. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return (src: string) => resolveProtocolImageSrc(src, jobId, watchPath);
  });

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly modalStack = inject(ModalStackService);
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;
  private portalRef: OverlayPortalRef | null = null;
  private positionRef: ConnectedOverlayPositionRef | null = null;

  readonly panelPos = signal<{ left: number; top: number }>({ left: 0, top: 0 });

  @ViewChild('triggerBtn') private triggerBtnRef?: ElementRef<HTMLButtonElement>;
  @ViewChild('portalRoot') private portalRootRef?: ElementRef<HTMLDivElement>;
  @ViewChild('panelEl') private panelElRef?: ElementRef<HTMLDivElement>;

  toggle(event: Event): void {
    event.stopPropagation();
    if (!this.hasPrompt()) return;
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
    const portalRoot = this.portalRootRef?.nativeElement;
    if (portalRoot && event.target instanceof Node && portalRoot.contains(event.target)) return;
    this.close();
  }

  // Escape routes through ModalStack so a real modal above wins first and the
  // detail panel does not close behind us.
  private readonly stackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.modalStackDispose) {
      this.modalStackDispose = this.modalStack.push('task-prompt-popover', () => {
        this.close();
        return true;
      });
      queueMicrotask(() => this.acquirePortalAndPosition());
    } else if (!isOpen && this.modalStackDispose) {
      this.releasePortal();
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly stackTeardown = this.destroyRef.onDestroy(() => {
    this.releasePortal();
    this.modalStackDispose?.();
  });

  private acquirePortalAndPosition(): void {
    if (!this.open()) return;
    if (!this.portalRef) {
      const root = this.portalRootRef?.nativeElement;
      if (!root) return;
      this.portalRef = this.overlayPortal.attachPanel(root);
    }
    const trigger = this.triggerBtnRef?.nativeElement;
    const panel = this.panelElRef?.nativeElement;
    if (!trigger || !panel) return;
    this.positionRef?.dispose();
    this.positionRef = this.overlayPortal.watchConnectedPosition(trigger, panel, {
      preferredPlacement: 'below',
      alignment: 'start',
      gap: 6,
      viewportPadding: 8,
    }, pos => this.panelPos.set({ left: pos.left, top: pos.top }));
  }

  private releasePortal(): void {
    this.positionRef?.dispose();
    this.positionRef = null;
    this.portalRef?.dispose();
    this.portalRef = null;
  }
}
