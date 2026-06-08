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
} from '@angular/core';
import { MarkdownViewComponent } from '../../../../../components/markdown-view/markdown-view.component';
import { TooltipDirective } from '../../../../../components/tooltip';
import { OverlayPortalDirective } from '../../../../../directives/overlay-portal.directive';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { resolveProtocolImageSrc } from '../../protocol-pane/protocol-image-resolver';

/**
 * "Prompt" affordance for the Overview tab. A small trigger next to the task
 * title opens a centered, read-only modal that renders the task prompt
 * (`prompt.md` / `promptMarkdown`) as Markdown via the shared
 * {@link MarkdownViewComponent}. The Overview tab summarises status, agent,
 * tokens and pipeline but never the prompt itself; this is the focused way to
 * read what the task was actually asked to do without leaving for the Files tab.
 *
 * The modal is portaled to the central modal overlay layer so it stays above
 * sibling panes and out of ancestor overflow clipping. Closes on backdrop click,
 * close button, and Escape routed through {@link ModalStackService} so another
 * modal above wins first and Escape does not close the detail panel behind it.
 * The body is scrollable for long prompts. The trigger hides itself when there
 * is no prompt text so it never opens an empty modal.
 */
@Component({
  selector: 'app-task-prompt-popover',
  standalone: true,
  imports: [MarkdownViewComponent, OverlayPortalDirective, TooltipDirective],
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
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;

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
    } else if (!isOpen && this.modalStackDispose) {
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly stackTeardown = this.destroyRef.onDestroy(() => {
    this.modalStackDispose?.();
  });
}
