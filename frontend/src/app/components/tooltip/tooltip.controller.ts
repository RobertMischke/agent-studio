import { Injectable } from '@angular/core';
import DOMPurify from 'dompurify';
import {
  StructuredTooltip,
  TooltipInput,
  TooltipPosition,
  TooltipSeverity
} from './tooltip.types';

const STYLE_ID = 'app-tooltip-styles';
const ALLOWED_TAGS = [
  'b', 'strong', 'i', 'em', 'u', 'code', 'kbd', 'br', 'p', 'small',
  'ul', 'ol', 'li',
  'table', 'thead', 'tbody', 'tr', 'th', 'td',
  'span', 'div'
];
const ALLOWED_ATTR = ['class'];

const GAP = 8;
const VIEWPORT_PAD = 6;
const ARROW_SIZE = 6;

interface Placement {
  side: 'top' | 'bottom' | 'left' | 'right';
  left: number;
  top: number;
  arrow: { left?: number; top?: number; right?: number; bottom?: number };
}

@Injectable({ providedIn: 'root' })
export class TooltipController {
  private host: HTMLDivElement | null = null;
  private arrow: HTMLDivElement | null = null;
  private title: HTMLDivElement | null = null;
  private body: HTMLDivElement | null = null;
  private currentAnchor: HTMLElement | null = null;

  show(
    anchor: HTMLElement,
    input: TooltipInput,
    position: TooltipPosition = 'auto',
    severity?: TooltipSeverity
  ): void {
    const content = normalize(input);
    if (!content) return;
    const host = this.ensure();
    this.currentAnchor = anchor;

    if (content.title) {
      this.title!.textContent = content.title;
      this.title!.style.display = '';
    } else {
      this.title!.textContent = '';
      this.title!.style.display = 'none';
    }

    const clean = DOMPurify.sanitize(content.body, {
      ALLOWED_TAGS,
      ALLOWED_ATTR
    });
    // Always go through innerHTML so HTML entities (`&amp;`, `&lt;`) decode
    // correctly. CSS `white-space: pre-line` preserves explicit newlines in
    // both plain and HTML mode. DOMPurify already stripped anything risky.
    this.body!.innerHTML = clean;

    host.classList.remove(
      'app-tooltip--info',
      'app-tooltip--warn',
      'app-tooltip--error',
      'app-tooltip--success'
    );
    if (severity) host.classList.add(`app-tooltip--${severity}`);

    host.style.visibility = 'hidden';
    host.style.opacity = '0';
    host.style.left = '0px';
    host.style.top = '0px';

    const placement = this.computePlacement(anchor, host, position);
    host.dataset['placement'] = placement.side;
    host.style.left = `${Math.round(placement.left)}px`;
    host.style.top = `${Math.round(placement.top)}px`;

    const arrowStyle = this.arrow!.style;
    arrowStyle.left = placement.arrow.left == null ? '' : `${placement.arrow.left}px`;
    arrowStyle.top = placement.arrow.top == null ? '' : `${placement.arrow.top}px`;
    arrowStyle.right = placement.arrow.right == null ? '' : `${placement.arrow.right}px`;
    arrowStyle.bottom = placement.arrow.bottom == null ? '' : `${placement.arrow.bottom}px`;

    host.style.visibility = 'visible';
    host.style.opacity = '1';
  }

  hide(anchor: HTMLElement | null): void {
    if (anchor && this.currentAnchor && anchor !== this.currentAnchor) return;
    if (!this.host) return;
    this.host.style.opacity = '0';
    this.host.style.visibility = 'hidden';
    this.currentAnchor = null;
  }

  private ensure(): HTMLDivElement {
    if (this.host && document.body.contains(this.host)) return this.host;
    this.installStyles();

    const host = document.createElement('div');
    host.className = 'app-tooltip';
    host.setAttribute('role', 'tooltip');
    host.setAttribute('data-testid', 'app-tooltip');

    const arrow = document.createElement('div');
    arrow.className = 'app-tooltip__arrow';
    arrow.setAttribute('aria-hidden', 'true');

    const title = document.createElement('div');
    title.className = 'app-tooltip__title';
    title.style.display = 'none';

    const body = document.createElement('div');
    body.className = 'app-tooltip__body';

    host.append(arrow, title, body);
    document.body.appendChild(host);

    this.host = host;
    this.arrow = arrow;
    this.title = title;
    this.body = body;
    return host;
  }

  private installStyles(): void {
    if (document.getElementById(STYLE_ID)) return;
    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = TOOLTIP_CSS;
    document.head.appendChild(style);
  }

  private computePlacement(
    anchor: HTMLElement,
    host: HTMLDivElement,
    preferred: TooltipPosition
  ): Placement {
    const aRect = anchor.getBoundingClientRect();
    const hRect = host.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    const order: Array<Exclude<TooltipPosition, 'auto'>> =
      preferred === 'auto'
        ? ['bottom', 'top', 'right', 'left']
        : [preferred, ...(['bottom', 'top', 'right', 'left'] as const).filter(p => p !== preferred)];

    for (const side of order) {
      const candidate = placeSide(side, aRect, hRect, vw, vh);
      if (candidate) return candidate;
    }

    // Fallback: clamp to viewport on the preferred side.
    const side = order[0];
    return placeSide(side, aRect, hRect, vw, vh, true)!;
  }
}

function placeSide(
  side: Exclude<TooltipPosition, 'auto'>,
  a: DOMRect,
  h: DOMRect,
  vw: number,
  vh: number,
  force = false
): Placement | null {
  let top = 0;
  let left = 0;

  switch (side) {
    case 'top':
      top = a.top - h.height - GAP;
      left = a.left + a.width / 2 - h.width / 2;
      break;
    case 'bottom':
      top = a.bottom + GAP;
      left = a.left + a.width / 2 - h.width / 2;
      break;
    case 'left':
      top = a.top + a.height / 2 - h.height / 2;
      left = a.left - h.width - GAP;
      break;
    case 'right':
      top = a.top + a.height / 2 - h.height / 2;
      left = a.right + GAP;
      break;
  }

  const fits =
    top >= VIEWPORT_PAD &&
    left >= VIEWPORT_PAD &&
    top + h.height <= vh - VIEWPORT_PAD &&
    left + h.width <= vw - VIEWPORT_PAD;

  if (!fits && !force) return null;

  // Clamp into viewport.
  if (left < VIEWPORT_PAD) left = VIEWPORT_PAD;
  if (top < VIEWPORT_PAD) top = VIEWPORT_PAD;
  if (left + h.width > vw - VIEWPORT_PAD) left = vw - VIEWPORT_PAD - h.width;
  if (top + h.height > vh - VIEWPORT_PAD) top = vh - VIEWPORT_PAD - h.height;

  const arrow = arrowFor(side, a, { top, left, width: h.width, height: h.height });
  return { side, top, left, arrow };
}

function arrowFor(
  side: Exclude<TooltipPosition, 'auto'>,
  anchor: DOMRect,
  host: { top: number; left: number; width: number; height: number }
): Placement['arrow'] {
  const anchorCenterX = anchor.left + anchor.width / 2;
  const anchorCenterY = anchor.top + anchor.height / 2;
  const clamp = (value: number, min: number, max: number) =>
    Math.max(min, Math.min(max, value));

  if (side === 'top' || side === 'bottom') {
    const arrowX = clamp(
      anchorCenterX - host.left - ARROW_SIZE,
      ARROW_SIZE + 2,
      host.width - ARROW_SIZE * 2 - 2
    );
    return side === 'top'
      ? { left: arrowX, bottom: -ARROW_SIZE }
      : { left: arrowX, top: -ARROW_SIZE };
  }
  const arrowY = clamp(
    anchorCenterY - host.top - ARROW_SIZE,
    ARROW_SIZE + 2,
    host.height - ARROW_SIZE * 2 - 2
  );
  return side === 'left'
    ? { top: arrowY, right: -ARROW_SIZE }
    : { top: arrowY, left: -ARROW_SIZE };
}

function normalize(input: TooltipInput): StructuredTooltip | null {
  if (input == null) return null;
  if (typeof input === 'string') {
    const trimmed = input.trim();
    if (!trimmed) return null;
    return { body: input };
  }
  if (!input.body || !input.body.trim()) return null;
  return input;
}

const TOOLTIP_CSS = `
.app-tooltip {
  position: fixed;
  z-index: 10000;
  pointer-events: none;
  max-width: 360px;
  min-width: 0;
  background: rgba(15, 17, 28, 0.96);
  color: #e2e8f0;
  border: 1px solid rgba(148, 163, 184, 0.28);
  border-radius: 10px;
  padding: 10px 12px;
  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
  font-size: 12.5px;
  line-height: 1.5;
  letter-spacing: 0.01em;
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.45), 0 2px 6px rgba(0, 0, 0, 0.35);
  backdrop-filter: blur(6px) saturate(120%);
  -webkit-backdrop-filter: blur(6px) saturate(120%);
  opacity: 0;
  visibility: hidden;
  transition: opacity 90ms ease-out;
}
.app-tooltip__title {
  font-size: 12px;
  font-weight: 600;
  color: #f8fafc;
  margin: 0 0 4px 0;
  letter-spacing: 0.02em;
}
.app-tooltip__body {
  font-size: 12.5px;
  color: #e2e8f0;
  white-space: pre-line;
}
.app-tooltip__body > * + * { margin-top: 6px; }
.app-tooltip__body ul,
.app-tooltip__body ol {
  margin: 0;
  padding-left: 18px;
}
.app-tooltip__body ul li,
.app-tooltip__body ol li { margin-top: 2px; }
.app-tooltip__body code {
  background: rgba(148, 163, 184, 0.18);
  border-radius: 3px;
  padding: 0 4px;
  font-family: 'JetBrains Mono', 'Consolas', monospace;
  font-size: 11.5px;
}
.app-tooltip__body kbd {
  background: rgba(148, 163, 184, 0.2);
  border: 1px solid rgba(148, 163, 184, 0.35);
  border-bottom-width: 2px;
  border-radius: 3px;
  padding: 0 5px;
  font-family: 'JetBrains Mono', 'Consolas', monospace;
  font-size: 11px;
}
.app-tooltip__body table {
  border-collapse: collapse;
  font-size: 11.5px;
}
.app-tooltip__body th,
.app-tooltip__body td {
  border-bottom: 1px solid rgba(148, 163, 184, 0.18);
  padding: 3px 6px;
  text-align: left;
}
.app-tooltip__body th { font-weight: 600; color: #f1f5f9; }
.app-tooltip__arrow {
  position: absolute;
  width: ${ARROW_SIZE * 2}px;
  height: ${ARROW_SIZE * 2}px;
  background: inherit;
  border: inherit;
  border-bottom: none;
  border-right: none;
  transform: rotate(45deg);
  box-shadow: -1px -1px 0 0 rgba(148, 163, 184, 0.05);
}
.app-tooltip[data-placement="top"] .app-tooltip__arrow { transform: rotate(225deg); }
.app-tooltip[data-placement="right"] .app-tooltip__arrow { transform: rotate(315deg); }
.app-tooltip[data-placement="bottom"] .app-tooltip__arrow { transform: rotate(45deg); }
.app-tooltip[data-placement="left"] .app-tooltip__arrow { transform: rotate(135deg); }
.app-tooltip--info { border-color: rgba(96, 165, 250, 0.55); }
.app-tooltip--info .app-tooltip__title { color: #93c5fd; }
.app-tooltip--warn { border-color: rgba(251, 191, 36, 0.55); }
.app-tooltip--warn .app-tooltip__title { color: #fbbf24; }
.app-tooltip--error { border-color: rgba(248, 113, 113, 0.6); }
.app-tooltip--error .app-tooltip__title { color: #fca5a5; }
.app-tooltip--success { border-color: rgba(74, 222, 128, 0.55); }
.app-tooltip--success .app-tooltip__title { color: #86efac; }
@media (prefers-reduced-motion: reduce) {
  .app-tooltip { transition: none; }
}
`;
