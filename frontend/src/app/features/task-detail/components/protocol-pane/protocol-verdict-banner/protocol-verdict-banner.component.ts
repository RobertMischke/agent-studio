import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { ProtocolVerdict } from '../protocol-verdict';
import type { ChainEvidenceLink, VerdictChain } from '../protocol-verdict-chain';

/**
 * The three-state verdict pill at the top of the protocol pane, plus the
 * collapsed "superseded run outcome" history line beneath it.
 *
 * Split out of `protocol-pane` so the reason-expansion state (BEFUND 1) and
 * the superseded-blocker rendering (BEFUND 2/3) live in one small, testable
 * surface instead of growing the already-oversized parent.
 *
 * - **Full reason (BEFUND 1):** the reason can be a long blocker sentence.
 *   Collapsed it clamps to one line with the whole text in the tooltip;
 *   clicking it unclamps so it wraps in full. The affordance only appears when
 *   there is genuinely more to read (a superseded blocker or a long reason).
 * - **Superseded history (BEFUND 2/3):** when the head verdict was demoted
 *   because the card reached an accepted stand, `verdict.superseded` carries
 *   the earlier Blocked/Failed outcome. It renders as a quiet history strip,
 *   never as the head banner, and expands together with the reason so the user
 *   can follow the sequence (accepted stand leads, superseded run below).
 */
@Component({
  selector: 'app-protocol-verdict-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './protocol-verdict-banner.component.html',
  styleUrl: './protocol-verdict-banner.component.scss',
})
export class ProtocolVerdictBannerComponent {
  readonly verdict = input.required<ProtocolVerdict>();

  /**
   * The four-step verdict chain (Run → Gate → Review → Lane) with evidence
   * links (BEFUND 2) plus the causal narrative (BEFUND 3). Optional: when null
   * the banner renders just the head pill (e.g. no run yet). Rendered inside
   * the expandable region so it opens together with the reason/history.
   */
  readonly chain = input<VerdictChain | null>(null);

  /** Emitted when the user clicks an evidence link in the chain. */
  readonly openEvidence = output<ChainEvidenceLink>();

  /** Whether the reason (and any superseded history) is shown in full. */
  readonly expanded = signal(false);

  /**
   * Only offer the expand affordance when there is genuinely more to read: a
   * demoted/superseded blocker, or a reason long enough to clip on one line.
   */
  readonly expandable = computed<boolean>(() => {
    const v = this.verdict();
    return !!v.superseded || !!this.chain() || (v.detail?.length ?? 0) > 64;
  });

  constructor() {
    // Collapse again whenever the verdict content changes (task switch or a
    // fresh summary) so a long reason does not leave the next banner expanded.
    let previousKey = '';
    effect(() => {
      const v = this.verdict();
      const key = `${v.label}::${v.detail}`;
      if (key !== previousKey) {
        previousKey = key;
        this.expanded.set(false);
      }
    });
  }

  toggle(): void {
    this.expanded.update((x) => !x);
  }

  onEvidence(link: ChainEvidenceLink): void {
    this.openEvidence.emit(link);
  }
}
