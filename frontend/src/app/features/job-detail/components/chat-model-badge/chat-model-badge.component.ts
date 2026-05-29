import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import { CLI_TYPES } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../cli';
import {
  cliTypeIcon as fmtCliTypeIcon,
  cliTypeLabel as fmtCliTypeLabel,
} from '../../../../services/format.util';
// `fmtCliTypeIcon` is used for the badge's leading glyph (not a menu surface);
// the model menu itself is text-only per the "Menu surfaces are text-only"
// convention.
import {
  buildModelMenuItems,
  cliTypeFromMenuId,
  currentBadgeText,
  isCliMenuId,
  isModelMenuId,
  modelBadgeTooltip,
  modelIdFromMenuId,
  shortModelName,
} from '../protocol-pane/protocol-pane/model-badge-menu-builders';
import type { MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { MenuComponent } from '../../../../components/menu';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * F44 — Subtle model + CLI badge sitting on the left of the chat-compose
 * action row. Right-click or left-click opens a shared <app-menu> with the
 * available models for the active CLI plus the four CLI choices. Disabled
 * while a run is in flight (tooltip explains why); the display stays
 * visible so the operator can always read "which model is on this task".
 *
 * The component is purely presentational: callers feed it the current
 * cliType / model / model catalog and react to `modelChange` /
 * `cliTypeChange` outputs. Empty-string `modelChange` payload means
 * "clear back to the CLI default" — same convention as the legacy
 * commandbar dropdown so the host's `setJobModel` call site stays
 * uniform.
 */
@Component({
  selector: 'app-chat-model-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MenuComponent, TooltipDirective],
  templateUrl: './chat-model-badge.component.html',
  styleUrls: ['./chat-model-badge.component.scss'],
})
export class ChatModelBadgeComponent {
  readonly cliType = input<CliType | null>(null);
  readonly model = input<string | null>(null);
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** True while a run is in flight; click is suppressed, tooltip explains why. */
  readonly disabled = input<boolean>(false);

  readonly modelChange = output<string>();
  readonly cliTypeChange = output<CliType>();

  /** Viewport-relative anchor for the open menu; null when closed. */
  readonly menuPosition = signal<{ x: number; y: number } | null>(null);
  readonly menuOpen = computed(() => this.menuPosition() !== null);

  readonly disabledReason = computed<string | null>(() =>
    this.disabled() ? 'Stop the run first to change the model.' : null,
  );

  readonly displayName = computed<string>(() => shortModelName(this.model()));

  readonly cliEmoji = computed<string>(() => {
    const t = this.cliType();
    return t ? fmtCliTypeIcon(t) : '·';
  });

  readonly tooltip = computed<string>(() =>
    modelBadgeTooltip(
      { cliType: this.cliType(), model: this.model(), cliTypeLabel: fmtCliTypeLabel },
      this.disabledReason(),
    ),
  );

  readonly ariaLabel = computed<string>(
    () =>
      `Model: ${currentBadgeText({
        cliType: this.cliType(),
        model: this.model(),
        cliTypeLabel: fmtCliTypeLabel,
      })}`,
  );

  readonly menuItems = computed<readonly MenuItem[]>(() =>
    buildModelMenuItems({
      cliType: this.cliType(),
      model: this.model(),
      availableModels: this.availableModels(),
      cliTypes: CLI_TYPES,
      cliTypeLabel: fmtCliTypeLabel,
    }),
  );

  openMenu(event: MouseEvent): void {
    if (this.disabledReason() !== null) return;
    event.preventDefault();
    event.stopPropagation();
    const target = event.currentTarget as HTMLElement | null;
    if (target) {
      const rect = target.getBoundingClientRect();
      this.menuPosition.set({ x: rect.left, y: rect.bottom + 4 });
    } else {
      this.menuPosition.set({ x: event.clientX, y: event.clientY });
    }
  }

  closeMenu(): void {
    this.menuPosition.set(null);
  }

  onMenuItemClick(ev: MenuItemClickEvent): void {
    if (isModelMenuId(ev.id)) {
      const id = modelIdFromMenuId(ev.id);
      if (id !== null) this.modelChange.emit(id);
      return;
    }
    if (isCliMenuId(ev.id)) {
      const t = cliTypeFromMenuId(ev.id);
      if (t) this.cliTypeChange.emit(t);
    }
  }
}
