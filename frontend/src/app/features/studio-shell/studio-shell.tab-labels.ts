/**
 * Pure label helpers for the studio-shell tab strip. Kept out of
 * studio-shell.component.ts so the component's TypeScript size stays under
 * its baseline budget, same reason as studio-shell.menu-builders.ts.
 */
import type { StudioTab } from './studio-shell.types';

export interface CompactTabLabelInputs {
  /** The tab's full strip label (`tabLabel`). */
  fullLabel: string;
  /** The leading num chip the strip renders, when there is one (`tabNum`). */
  num: string | null;
  /** Short user-facing id for task-bearing tabs. */
  taskId: string;
}

/**
 * Compact label for a pinned tab (AGT-2672). A pinned tab trades its full
 * title for the shortest string that still identifies the target, so a row of
 * pins stays scannable instead of eating the strip. The untruncated label
 * remains reachable through the tooltip and `aria-label`.
 *
 * Returns `''` when the leading dot / num / icon already identifies the tab on
 * its own - a pinned task tab shows its key in the num chip, so repeating it
 * as a title would just be noise.
 */
export function compactTabLabel(tab: StudioTab, input: CompactTabLabelInputs): string {
  switch (tab.kind) {
    case 'task':
      return input.num ? '' : input.taskId;
    case 'activity':
      return input.taskId;
    case 'diff':
      return tab.commitSha.slice(0, 7);
    case 'feed':
      return 'Activity';
    case 'chat-history':
      return 'Chat';
    case 'workspace-settings':
      return 'Settings';
    default: {
      // Project-scoped labels read "<short code> · <surface>". The short code
      // alone is the compact form; the leading icon carries the surface.
      // Labels without a separator are already short.
      const separator = input.fullLabel.indexOf(' · ');
      return separator > 0 ? input.fullLabel.slice(0, separator) : input.fullLabel;
    }
  }
}
