import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { markdownToHtml } from '../markdown-utils';
import { RoleBadgeComponent } from '../../features/workforce';

/**
 * Shared chat-row presentation. Renders one message-or-event row inside
 * any chat surface: the orchestrator/task chat (`<app-chat>`) and the
 * virtualised project chat list (`<app-project-chat-list>`). The row
 * carries the bits both surfaces care about — role badge, kind label,
 * timestamp, markdown body — so each consumer can lift its inline
 * `<article>` markup into one place.
 *
 * The two consumer types diverge in fields (`ChatMessage` vs
 * `ProjectChatTurn`); each consumer adapts its payload to this row's
 * input shape before binding. See `frontend/AGENTS.md` for the wider
 * unification plan.
 */
export type ChatRowAuthor =
  | 'user'
  | 'orchestrator'
  | 'agent'
  | 'supervisor'
  | 'claude'
  | 'codex'
  | 'copilot'
  | 'gemini'
  | 'system';

export interface ChatRowInput {
  /** Backing model id (for tracking / data-testid). */
  id: string;
  author: ChatRowAuthor;
  /** Optional override for display label (e.g. show "You" for user). */
  authorLabel?: string;
  /** Optional kind/category, rendered as a monospace chip after the author. */
  kind?: string | null;
  /** Optional file refs rendered alongside the role badge. */
  refs?: readonly string[] | null;
  /** ISO 8601. */
  ts: string;
  /** Markdown source (rendered to HTML). For pre-escaped/plain text, pass
   *  `bodyHtml` instead and leave this empty. */
  body?: string;
  /** Already-sanitized HTML; takes precedence over `body`. */
  bodyHtml?: SafeHtml | string | null;
  /** Reserved for future "show more" collapsing. */
  collapsed?: boolean;
  /** True when the row is in the user variant (different bubble colour). */
  userVariant?: boolean;
  /** True when this row is an event card (dashed border, dimmer body). */
  eventVariant?: boolean;
  /** Temporary highlight state (e.g. just navigated-to in search). */
  flash?: boolean;
}

@Component({
  selector: 'app-chat-row',
  standalone: true,
  imports: [CommonModule, RoleBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-row.component.html',
  styleUrl: './chat-row.component.scss',
})
export class ChatRowComponent {
  readonly row = input.required<ChatRowInput>();

  private readonly sanitizer = inject(DomSanitizer);

  readonly bodySafe = computed<SafeHtml>(() => {
    const r = this.row();
    if (r.bodyHtml != null) {
      return typeof r.bodyHtml === 'string'
        ? this.sanitizer.bypassSecurityTrustHtml(r.bodyHtml)
        : r.bodyHtml;
    }
    return this.sanitizer.bypassSecurityTrustHtml(markdownToHtml(r.body ?? ''));
  });

  readonly formattedTs = computed<string>(() => {
    const ts = this.row().ts;
    if (!ts) return '';
    try {
      return new Date(ts).toLocaleString();
    } catch {
      return ts;
    }
  });
}
