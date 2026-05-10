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
  templateUrl: './info-button.component.html',
  styleUrl: './info-button.component.scss'
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
