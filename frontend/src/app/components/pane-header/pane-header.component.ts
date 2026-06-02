import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * The single canonical panel-header control. Two render modes from one
 * component so the studio sidebar and the detail-view panes share one
 * chrome (this absorbed the former `app-section-header`):
 *
 *   - Pane mode (default): icon + title + actions slot + maximize / hide
 *     buttons used by the prompt / protocol / git panes. The prompt
 *     pane's three-tab strip renders inside the projected `tabs` slot
 *     instead of the title; pass an empty title in that case and project
 *     the tabs.
 *   - Collapsible section mode (`[collapsible]="true"`): a compact
 *     uppercase header used for the sidebar/explorer panel headers
 *     (Workspaces, Open tabs). The whole row is a toggle button — a
 *     leading chevron flips between `chevronDown` (expanded) and
 *     `chevronRight` (collapsed) and `collapsedChange` fires the flipped
 *     state so the parent can persist it (see ExplorerSectionsService).
 *
 * See docs/frontend-scss-quality.md "Wave C".
 */
@Component({
  selector: 'app-pane-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './pane-header.component.html',
  styleUrl: './pane-header.component.scss',
})
export class PaneHeaderComponent {
  readonly icon = input<StudioIconName | null>(null);
  /**
   * Optional emoji/glyph icon. Used by panes whose title still carries a
   * Unicode glyph (e.g. the protocol pane's "📜 Protocol"). Renders next
   * to or instead of {@link icon} so callers can opt into the legacy
   * emoji vocabulary without losing the shared chrome.
   */
  readonly iconEmoji = input<string | null>(null);
  readonly title = input('');
  readonly maximized = input(false);
  readonly maximizable = input(true);
  readonly hideable = input(true);
  /** Optional extra subtitle that follows the title. */
  readonly subtitle = input<string | null>(null);
  /** data-testid passthrough. */
  readonly testid = input<string | null>(null);
  /** Optional modifier class applied to the host header (e.g. brand wash). */
  readonly modifier = input<string | null>(null);

  readonly maximize = output<void>();
  readonly hide = output<void>();

  /**
   * Collapsible section mode. When true the header renders the compact
   * uppercase toggle (chevron + title + count + actions slot) used by the
   * sidebar/explorer panel headers instead of the pane chrome above. The
   * pane-mode maximize / hide buttons are not rendered in this mode.
   */
  readonly collapsible = input(false);
  readonly collapsed = input(false);
  /** Optional count badge shown after the title in section mode. */
  readonly count = input<string | number | null>(null);
  readonly collapsedChange = output<boolean>();

  onCollapseToggle(ev: Event): void {
    ev.stopPropagation();
    this.collapsedChange.emit(!this.collapsed());
  }
}
