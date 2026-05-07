import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { InstantTooltipDirective } from '../../directives/instant-tooltip.directive';
import { markdownToHtml } from '../markdown-utils';

interface ConceptDocPayload {
  readonly topic: string;
  readonly title: string;
  readonly body: string;
}

/**
 * Subtle "i" trigger that opens a side-drawer with the rendered concept
 * doc for the given topic. The doc body is fetched on first open from
 * <c>GET /api/concept-docs/{topic}</c>; the FE never duplicates the
 * prose. Reuses the markdown renderer the chat surface uses.
 *
 * Selective placement: this component is opt-in per surface. Wire it in
 * lane headers (or anywhere else) only when the behaviour is non-obvious.
 * Adding it everywhere would clutter; that's the explicit anti-pattern
 * called out in the design.
 */
@Component({
  selector: 'app-info-button',
  standalone: true,
  imports: [InstantTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button"
            class="info-btn"
            [attr.data-testid]="'info-button-' + topic()"
            [attr.aria-expanded]="open()"
            [attr.aria-label]="'How does this lane work?'"
            [appTip]="'How does this lane work?'"
            (click)="toggle($event)">i</button>
    @if (open()) {
      <div class="info-btn__backdrop"
           [attr.data-testid]="'info-button-backdrop-' + topic()"
           (click)="close()"></div>
      <aside class="info-btn__drawer"
             role="dialog"
             [attr.aria-label]="title()"
             [attr.data-testid]="'info-button-drawer-' + topic()">
        <header class="info-btn__head">
          <h2 class="info-btn__title"
              [attr.data-testid]="'info-button-title-' + topic()">{{ title() }}</h2>
          <button type="button"
                  class="info-btn__close"
                  [attr.data-testid]="'info-button-close-' + topic()"
                  aria-label="Close"
                  (click)="close()">×</button>
        </header>
        <div class="info-btn__body"
             [attr.data-testid]="'info-button-body-' + topic()">
          @if (loading()) {
            <p class="info-btn__loading">Loading…</p>
          } @else if (errorMessage(); as msg) {
            <p class="info-btn__error">{{ msg }}</p>
          } @else if (bodyHtml(); as html) {
            <div class="info-btn__rendered" [innerHTML]="html"></div>
          }
        </div>
      </aside>
    }
  `,
  styles: [`
    :host { display: inline-flex; align-items: center; }

    .info-btn {
      display: inline-grid;
      place-items: center;
      width: 18px;
      height: 18px;
      padding: 0;
      border-radius: 50%;
      border: 1px solid rgba(148, 163, 184, 0.45);
      background: transparent;
      color: rgba(226, 232, 240, 0.78);
      font-family: Georgia, 'Times New Roman', serif;
      font-style: italic;
      font-size: 11px;
      line-height: 1;
      cursor: pointer;
      transition: background 0.12s ease, border-color 0.12s ease, color 0.12s ease;
    }
    .info-btn:hover,
    .info-btn:focus-visible {
      border-color: rgba(148, 163, 184, 0.75);
      background: rgba(148, 163, 184, 0.14);
      color: #f1f5f9;
      outline: none;
    }

    .info-btn__backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.45);
      z-index: 200;
    }
    .info-btn__drawer {
      position: fixed;
      top: 0;
      right: 0;
      bottom: 0;
      width: min(460px, 94vw);
      background: #161624;
      color: #cdd6f4;
      border-left: 1px solid rgba(255, 255, 255, 0.08);
      z-index: 201;
      overflow-y: auto;
      padding: 1rem 1.25rem 1.5rem;
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
      font-size: 0.875rem;
      line-height: 1.55;
    }
    .info-btn__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.75rem;
      border-bottom: 1px solid rgba(255, 255, 255, 0.08);
      padding-bottom: 0.5rem;
    }
    .info-btn__title {
      font-size: 1rem;
      font-weight: 600;
      margin: 0;
      color: #f1f5f9;
    }
    .info-btn__close {
      background: transparent;
      border: none;
      color: inherit;
      font-size: 1.5rem;
      line-height: 1;
      cursor: pointer;
      padding: 0 4px;
    }
    .info-btn__close:hover { color: #f1f5f9; }
    .info-btn__body { flex: 1 1 auto; }
    .info-btn__loading { color: rgba(205, 214, 244, 0.6); }
    .info-btn__error   { color: #f38ba8; }

    /* Match the chat surface's markdown look without copying its rules
       wholesale: paragraphs and lists need legible spacing inside the
       drawer, code chips stay subtle. */
    .info-btn__rendered :first-child { margin-top: 0; }
    .info-btn__rendered :last-child  { margin-bottom: 0; }
    .info-btn__rendered p { margin: 0 0 0.65rem; }
    .info-btn__rendered ul,
    .info-btn__rendered ol { margin: 0 0 0.75rem; padding-left: 1.25rem; }
    .info-btn__rendered li { margin: 0.25rem 0; }
    .info-btn__rendered h1,
    .info-btn__rendered h2,
    .info-btn__rendered h3 {
      margin: 1rem 0 0.4rem;
      color: #e2e8f0;
      font-weight: 600;
    }
    .info-btn__rendered h2 { font-size: 0.95rem; }
    .info-btn__rendered h3 { font-size: 0.875rem; text-transform: none; letter-spacing: 0; }
    .info-btn__rendered code {
      background: rgba(255, 255, 255, 0.06);
      padding: 1px 5px;
      border-radius: 3px;
      font-family: ui-monospace, monospace;
      font-size: 0.8125rem;
    }
    .info-btn__rendered a {
      color: #a5b4fc;
      text-decoration: none;
    }
    .info-btn__rendered a:hover { color: #c4b5fd; text-decoration: underline; }
  `]
})
export class InfoButtonComponent {
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);

  readonly topic = input.required<string>();

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  private readonly doc = signal<ConceptDocPayload | null>(null);

  readonly title = computed(() => this.doc()?.title ?? 'About this lane');
  readonly bodyHtml = computed<SafeHtml | null>(() => {
    const d = this.doc();
    if (!d) return null;
    return this.sanitizer.bypassSecurityTrustHtml(markdownToHtml(d.body));
  });

  toggle(event: MouseEvent): void {
    event.stopPropagation();
    if (this.open()) {
      this.close();
      return;
    }
    this.open.set(true);
    if (!this.doc() && !this.loading()) {
      void this.load();
    }
  }

  close(): void {
    this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set(null);
    try {
      const payload = await firstValueFrom(
        this.http.get<ConceptDocPayload>(`/api/concept-docs/${encodeURIComponent(this.topic())}`)
      );
      this.doc.set(payload);
    } catch (err: any) {
      const status = err?.status ?? 0;
      this.errorMessage.set(
        status === 404
          ? `No concept doc found for "${this.topic()}".`
          : 'Could not load this concept doc. Try again later.'
      );
    } finally {
      this.loading.set(false);
    }
  }
}
