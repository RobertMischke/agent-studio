/**
 * Pure decision function for the chat-history backfill behaviour.
 *
 * Recent window loads eagerly. Scrolling above triggers progressive
 * backfill — but only until the deep-history threshold. Past that, the
 * chat shows an explicit step-load panel and silent infinite scroll
 * stops. The threshold guards the browser: ten thousand turns streamed
 * automatically would freeze layout long before the user notices they
 * dragged the scrollbar too far.
 */

export type LoadAction = 'continue-backfill' | 'show-panel' | 'no-op';

export interface LoadStrategyInput {
  /** Number of turns currently loaded in the FE. */
  loadedCount: number;
  /** Whether the server is known to have more turns above the loaded range. */
  hasMoreOlder: boolean;
  /** True while a backfill request is already in flight. */
  isLoading: boolean;
  /** True when the viewport is within the trigger zone at the top. */
  isNearTop: boolean;
  /** Loaded-count past which the step-load panel takes over. */
  threshold: number;
}

export function decideLoadAction(input: LoadStrategyInput): LoadAction {
  if (!input.hasMoreOlder) return 'no-op';
  if (input.isLoading) return 'no-op';
  if (input.loadedCount >= input.threshold) return 'show-panel';
  if (!input.isNearTop) return 'no-op';
  return 'continue-backfill';
}

/**
 * Format a "you are viewing N of M, going back to <date>" sentence for
 * the step-load panel. Kept pure so the spec can lock the wording.
 */
export function formatLoadedSummary(
  loaded: number,
  total: number | null,
  oldestIso: string | null
): string {
  const pieces: string[] = [];
  if (total != null && total > 0) {
    pieces.push(`Viewing ${loaded.toLocaleString('en-US')} of ${total.toLocaleString('en-US')} messages.`);
  } else {
    pieces.push(`Viewing ${loaded.toLocaleString('en-US')} messages.`);
  }
  if (oldestIso) {
    try {
      const d = new Date(oldestIso);
      const back = d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });
      pieces.push(`Oldest loaded: ${back}.`);
    } catch {
      // fall through; bad date strings are not user-facing here
    }
  }
  return pieces.join(' ');
}
