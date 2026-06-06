import { Injectable } from '@angular/core';

export interface OverlayPortalRef {
  readonly element: HTMLElement;
  dispose(): void;
}

/**
 * Central append-to-body primitive for floating UI.
 *
 * Components keep their Angular templates and state, but the actual overlay
 * element is hoisted to <body> so it escapes ancestor overflow, transforms,
 * paint containment, and sibling stacking contexts.
 */
@Injectable({ providedIn: 'root' })
export class OverlayPortalService {
  attach(element: HTMLElement, layerClass = 'studio-overlay-layer'): OverlayPortalRef {
    const parent = element.parentNode;
    const next = element.nextSibling;
    const classes = layerClass.split(/\s+/).filter(Boolean);
    for (const cls of classes) element.classList.add(cls);
    document.body.appendChild(element);

    let disposed = false;
    return {
      element,
      dispose: () => {
        if (disposed) return;
        disposed = true;
        for (const cls of classes) element.classList.remove(cls);
        if (!parent || !element.isConnected) return;
        if (next && next.parentNode === parent) {
          parent.insertBefore(element, next);
        } else {
          parent.appendChild(element);
        }
      },
    };
  }
}
