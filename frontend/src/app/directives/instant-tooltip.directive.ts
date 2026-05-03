import { Directive, ElementRef, HostListener, Input, OnDestroy } from '@angular/core';

/**
 * Lightweight tooltip that shows immediately on hover (no native-title delay)
 * and renders multi-line content via `white-space: pre-line`. A single shared
 * floating element is reused across the page; positioning happens at show
 * time using getBoundingClientRect.
 *
 * Use `[appTip]` with a plain string. Newlines render as line breaks. The
 * element opts out of the native title attribute so the browser's own
 * tooltip never competes with this one.
 */
@Directive({
  selector: '[appTip]',
  standalone: true
})
export class InstantTooltipDirective implements OnDestroy {
  @Input('appTip') tipContent: string | null | undefined = '';

  constructor(private host: ElementRef<HTMLElement>) {}

  ngOnDestroy(): void {
    InstantTooltipController.hide(this.host.nativeElement);
  }

  @HostListener('mouseenter')
  onEnter() {
    const text = (this.tipContent ?? '').toString();
    if (!text) return;
    InstantTooltipController.show(this.host.nativeElement, text);
  }

  @HostListener('mouseleave')
  onLeave() {
    InstantTooltipController.hide(this.host.nativeElement);
  }

  @HostListener('click')
  onClick() {
    InstantTooltipController.hide(this.host.nativeElement);
  }
}

class InstantTooltipController {
  private static el: HTMLDivElement | null = null;
  private static currentAnchor: HTMLElement | null = null;

  private static ensure(): HTMLDivElement {
    if (this.el && document.body.contains(this.el)) return this.el;
    const d = document.createElement('div');
    d.setAttribute('data-testid', 'instant-tooltip');
    d.setAttribute('role', 'tooltip');
    Object.assign(d.style, {
      position: 'fixed',
      zIndex: '10000',
      pointerEvents: 'none',
      maxWidth: '360px',
      background: '#0b1020',
      color: '#e2e8f0',
      border: '1px solid rgba(148,163,184,0.25)',
      borderRadius: '8px',
      padding: '8px 10px',
      fontSize: '12px',
      lineHeight: '1.45',
      whiteSpace: 'pre-line',
      boxShadow: '0 8px 24px rgba(0,0,0,0.45)',
      opacity: '0',
      transform: 'translateY(2px)',
      transition: 'opacity 0.08s ease',
      visibility: 'hidden'
    } as CSSStyleDeclaration);
    document.body.appendChild(d);
    this.el = d;
    return d;
  }

  static show(anchor: HTMLElement, text: string) {
    const d = this.ensure();
    this.currentAnchor = anchor;
    d.textContent = text;
    d.style.visibility = 'hidden';
    d.style.opacity = '0';
    // Force layout so we can measure size before positioning.
    d.style.left = '0px';
    d.style.top = '0px';
    const rect = anchor.getBoundingClientRect();
    const tipRect = d.getBoundingClientRect();
    const margin = 8;
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    let top = rect.bottom + margin;
    if (top + tipRect.height > vh - 4) {
      top = rect.top - tipRect.height - margin;
    }
    if (top < 4) top = 4;

    let left = rect.left;
    if (left + tipRect.width > vw - 4) {
      left = vw - tipRect.width - 4;
    }
    if (left < 4) left = 4;

    d.style.left = `${Math.round(left)}px`;
    d.style.top = `${Math.round(top)}px`;
    d.style.visibility = 'visible';
    d.style.opacity = '1';
  }

  static hide(anchor: HTMLElement) {
    if (this.currentAnchor && anchor !== this.currentAnchor) return;
    const d = this.el;
    if (!d) return;
    d.style.opacity = '0';
    d.style.visibility = 'hidden';
    this.currentAnchor = null;
  }
}
