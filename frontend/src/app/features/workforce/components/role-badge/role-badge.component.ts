import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { getRole, resolveRole, type WorkforceRole, type WorkforceRoleId } from '../../models/workforce-role';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Small inline badge that identifies the workforce role for a chat row.
 *
 * Two input shapes are supported so callers can pass either a
 * pre-resolved role id or the raw author/kind/refs and let the
 * deterministic mapper pick. The badge renders a glyph + label and
 * exposes the role description via the canonical `[appTooltip]`
 * directive.
 */
@Component({
  selector: 'app-role-badge',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './role-badge.component.html',
  styleUrl: './role-badge.component.scss',
})
export class RoleBadgeComponent {
  /** Already-resolved role id; wins over author/kind/refs when set. */
  readonly roleId = input<WorkforceRoleId | null>(null);
  readonly author = input<string | null>(null);
  readonly kind = input<string | null>(null);
  readonly refs = input<readonly string[] | null>(null);
  /** Drops the label when true, leaving only the coloured glyph. */
  readonly compact = input(false);

  readonly role = computed<WorkforceRole>(() => {
    const id = this.roleId();
    if (id) return getRole(id);
    return resolveRole({
      author: this.author(),
      kind: this.kind(),
      refs: this.refs(),
    });
  });
}
