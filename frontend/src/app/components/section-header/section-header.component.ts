import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * Compact uppercase section heading used in the kanban (column__header),
 * the studio sidebar (.studio-explorer__group-head), the lane group
 * (.lane-group__head), and the project hub rail. Centralises the
 * icon + title + count + (projected) actions pattern.
 *
 *   ICON   SECTION TITLE   [count]   [actions slot]
 *
 * Usage:
 *   <app-section-header
 *     icon="grid"
 *     title="WORKSPACE"
 *     [count]="3">
 *     <button actions class="my-add-btn">+</button>
 *   </app-section-header>
 */
@Component({
  selector: 'app-section-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './section-header.component.html',
  styleUrl: './section-header.component.scss',
})
export class SectionHeaderComponent {
  readonly icon = input<StudioIconName | null>(null);
  readonly iconChar = input<string | null>(null);
  readonly title = input('');
  readonly count = input<string | number | null>(null);
  /** Render as an `<h2>` (default) for accessibility; switch to a `<button>`
   *  via `interactive` so the entire row becomes clickable. */
  readonly interactive = input(false);
  readonly active = input(false);
  readonly testid = input<string | null>(null);
}
