import { Directive, ElementRef, HostListener, inject } from '@angular/core';

// Keep the resize contract aligned with the wider shared catalogue, including
// AGT-2286's change/review columns. A narrow legacy rail clips that shared
// overview and jumps on the first pointer move.
const MIN_WIDTH = 448;
const MAX_WIDTH = 704;
const RESIZE_STEP = 16;

interface PromptNavResizeState {
  pointerId: number;
  startX: number;
  startWidth: number;
  target: HTMLElement;
}

@Directive({
  selector: '[appPromptNavSplitter]',
  standalone: true,
})
export class PromptNavSplitterDirective {
  private readonly host = inject(ElementRef<HTMLElement>);
  private resize: PromptNavResizeState | null = null;

  @HostListener('pointerdown', ['$event'])
  onPointerDown(event: PointerEvent): void {
    const target = this.target();
    if (!target) return;
    event.preventDefault();
    this.resize = { pointerId: event.pointerId, startX: event.clientX, startWidth: target.getBoundingClientRect().width, target };
    this.host.nativeElement.setPointerCapture(event.pointerId);
    this.host.nativeElement.classList.add('prompts__splitter--dragging');
    document.body.style.cursor = 'col-resize';
  }

  @HostListener('pointermove', ['$event'])
  onPointerMove(event: PointerEvent): void {
    const resize = this.resize;
    if (!resize || resize.pointerId !== event.pointerId) return;
    this.setWidth(resize.target, resize.startWidth + event.clientX - resize.startX);
  }

  @HostListener('pointerup', ['$event'])
  @HostListener('pointercancel', ['$event'])
  finishPointerResize(event: PointerEvent): void {
    if (!this.resize || this.resize.pointerId !== event.pointerId) return;
    this.host.nativeElement.releasePointerCapture(event.pointerId);
    this.resize = null;
    this.host.nativeElement.classList.remove('prompts__splitter--dragging');
    document.body.style.cursor = '';
  }

  @HostListener('keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const target = this.target();
    if (!target) return;
    const width = target.getBoundingClientRect().width;
    if (event.key === 'ArrowLeft') this.setWidth(target, width - RESIZE_STEP);
    else if (event.key === 'ArrowRight') this.setWidth(target, width + RESIZE_STEP);
    else if (event.key === 'Home') this.setWidth(target, MIN_WIDTH);
    else if (event.key === 'End') this.setWidth(target, MAX_WIDTH);
    else return;
    event.preventDefault();
  }

  private target(): HTMLElement | null {
    return this.host.nativeElement.previousElementSibling as HTMLElement | null;
  }

  private setWidth(target: HTMLElement, width: number): void {
    const clamped = Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, width));
    target.style.width = `${clamped}px`;
    this.host.nativeElement.setAttribute('aria-valuenow', `${Math.round(clamped)}`);
  }
}
