import { ChangeDetectionStrategy, Component, Input, ViewEncapsulation, output } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * Reusable tree row used in the studio shell sidebar (Explorer
 * workspace tree, Tasks outline, legacy `.tree-row`). Centralises the
 * chevron + glyph + label + count layout so the three places that
 * repeat the pattern read from one source.
 *
 * Outline:
 *   [chevron] [glyph] label [meta] [count badge]
 *
 * Caller chooses which slots to fill via inputs. Two visual variants:
 *   - level="root"  — workspace / project rows (8 px left padding)
 *   - level="child" — sub rows (44 px left padding to nest under the
 *                     parent's glyph)
 *
 * The component preserves the legacy BEM class names so existing
 * styles.scss bridge entries continue to apply without rewrite.
 */
@Component({
  selector: 'app-tree-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './tree-row.component.html',
  styleUrl: './tree-row.component.scss',
})
export class TreeRowComponent {
  /** Visible chevron — pass `null` to suppress the chevron column. */
  @Input() chevron: 'collapsed' | 'expanded' | null = null;
  /** Optional SVG glyph; takes precedence over `glyphChar`. */
  @Input() glyph: StudioIconName | null = null;
  /** Optional initial letter (e.g. project avatar "A"). */
  @Input() glyphChar: string | null = null;
  /** Coloured square behind the glyph initial. */
  @Input() glyphColor: string | null = null;
  @Input() label = '';
  @Input() count: string | number | null = null;
  @Input() meta: string | null = null;
  @Input() active = false;
  @Input() level: 'root' | 'child' = 'root';
  @Input() testid: string | null = null;

  readonly chevronClick = output<Event>();
  readonly selectRequest = output<Event>();
  readonly secondary = output<Event>();

  onChevronClick(ev: Event): void {
    ev.stopPropagation();
    this.chevronClick.emit(ev);
  }

  onSelect(ev: Event): void {
    this.selectRequest.emit(ev);
  }

  onSecondary(ev: Event): void {
    this.secondary.emit(ev);
  }
}
