import { Directive, ElementRef, OnDestroy, effect, inject, input } from '@angular/core';

@Directive({
  selector: '[appPipelineStepFocus]',
  standalone: true,
})
export class PipelineStepFocusDirective implements OnDestroy {
  readonly focusStepId = input<string | undefined>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly observer = new MutationObserver(() => this.focusMatchingRow());
  private focusedRow: HTMLDetailsElement | null = null;

  constructor() {
    this.observer.observe(this.host.nativeElement, { childList: true, subtree: true });
    effect(() => {
      this.focusStepId();
      queueMicrotask(() => this.focusMatchingRow());
    });
  }

  ngOnDestroy(): void {
    this.observer.disconnect();
  }

  private focusMatchingRow(): void {
    const stepId = this.focusStepId();
    if (!stepId) {
      this.focusedRow?.removeAttribute('aria-current');
      this.focusedRow = null;
      return;
    }

    const target = Array.from(
      this.host.nativeElement.querySelectorAll<HTMLDetailsElement>('details[data-testid]'),
    ).find(row => row.dataset['testid'] === `pipeline-step-row-${stepId}`);
    if (!target) {
      this.focusedRow?.removeAttribute('aria-current');
      this.focusedRow = null;
      return;
    }
    if (target === this.focusedRow) return;

    this.focusedRow?.removeAttribute('aria-current');
    this.focusedRow = target;
    target.setAttribute('aria-current', 'location');
    target.open = true;
    target.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
  }
}
