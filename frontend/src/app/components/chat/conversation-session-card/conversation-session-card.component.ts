import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { TooltipDirective } from '../../tooltip';
import type { ParsedRateLimit, SessionCardData } from '../conversation-session-meta';

/**
 * Compact session / rate-limit meta card for the next-gen conversation view
 * (`Frontend:NextGenChat`).
 *
 * The CLI's `Session init` and `Rate limit` lifecycle lines used to ride
 * along as an invisible tooltip on the first bubble header. The operator
 * asked for them to be rendered as a small, readable meta card (session id,
 * 5h / weekly window, status, reset clock) instead of raw bullet text. This
 * component owns that rendering; it is pure presentational and reads its
 * structured input from {@link SessionCardData} (built by the projection /
 * the conversation view's row builder).
 */
@Component({
  selector: 'app-conversation-session-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './conversation-session-card.component.html',
  styleUrl: './conversation-session-card.component.scss',
})
export class ConversationSessionCardComponent {
  readonly data = input.required<SessionCardData>();

  readonly rateLimit = computed<ParsedRateLimit | null>(() => this.data().rateLimit ?? null);

  formatTime(iso: string | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  /** Reset clock from the structured `resetsAt`, falling back to the human hint. */
  readonly resetClock = computed<string>(() => {
    const rl = this.rateLimit();
    if (!rl) return '';
    if (rl.resetsAtMs) {
      const d = new Date(rl.resetsAtMs);
      if (!Number.isNaN(d.getTime())) {
        return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      }
    }
    return rl.resetHint ?? '';
  });
}
