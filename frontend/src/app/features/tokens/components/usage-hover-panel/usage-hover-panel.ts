import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  effect,
  inject,
  output,
  signal,
} from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { HeaderQuotaComponent } from '../../../quota';
import { CliUsageStore } from '../../services/cli-usage.store';
import { CliUsageMiniPopoverComponent } from '../cli-usage-mini-popover/cli-usage-mini-popover';

/**
 * Status-bar quota trigger. Two interactions, two depths:
 *
 * - **Hover** pops a compact <app-cli-usage-mini-popover> with only the
 *   core number per CLI (primary window used% + headroom + current
 *   window). A glance, not the full dump.
 * - **Click / Enter** opens the CLI-Management panel (the "Settings-Dach")
 *   where the full <app-cli-usage-detail> lives, via the `openCliAdmin`
 *   output that bubbles through the status-bar to the app shell.
 *
 * The strip itself is delegated to <app-header-quota>. Data comes from
 * the shared `CliUsageStore`: this component only starts the lightweight
 * quota poll (`ensureQuotaStarted`) and reads `quotaRows`; the heavy
 * aggregates load lazily inside the CLI-Management panel.
 *
 * Hover open has a 120 ms grace (so a cursor flying across the bar does
 * not pop the popover) and a 220 ms close grace (so the user can move
 * onto the popover without losing it).
 */
@Component({
  selector: 'app-usage-hover-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HeaderQuotaComponent, CliUsageMiniPopoverComponent],
  templateUrl: './usage-hover-panel.html',
  styleUrl: './usage-hover-panel.scss'
})
export class UsageHoverPanelComponent implements OnInit, OnDestroy {
  private readonly store = inject(CliUsageStore);

  readonly open = signal(false);
  readonly quotaRows = this.store.quotaRows;

  readonly openCliAdmin = output<void>();

  private openTimer: ReturnType<typeof setTimeout> | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.store.ensureQuotaStarted();
  }

  ngOnDestroy(): void {
    if (this.openTimer != null) clearTimeout(this.openTimer);
    if (this.closeTimer != null) clearTimeout(this.closeTimer);
  }

  /** Click / Enter / Space: hand off to the full CLI-Management panel. */
  activate(ev?: Event): void {
    ev?.stopPropagation();
    this.closePopover();
    this.openCliAdmin.emit();
  }

  closePopover(): void {
    this.cancelOpen();
    this.cancelClose();
    this.open.set(false);
  }

  onTriggerKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    this.activate(event);
  }

  // ---- Hover gating with grace periods ----

  onAnchorEnter(): void { this.scheduleOpen(); }
  onAnchorLeave(): void { this.scheduleClose(); }
  onPopEnter(): void { this.cancelClose(); }
  onPopLeave(): void { this.scheduleClose(); }

  // Escape routes through ModalStack so a confirm/error dialog above
  // wins first. The popover registers itself only while it is open.
  private readonly modalStack = inject(ModalStackService);
  private readonly hoverDestroyRef = inject(DestroyRef);
  private hoverStackDispose: (() => void) | null = null;
  private readonly hoverStackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.hoverStackDispose) {
      this.hoverStackDispose = this.modalStack.push('usage-hover-panel', () => this.closePopover());
    } else if (!isOpen && this.hoverStackDispose) {
      this.hoverStackDispose();
      this.hoverStackDispose = null;
    }
  });
  private readonly hoverStackTeardown = this.hoverDestroyRef.onDestroy(() => this.hoverStackDispose?.());

  private scheduleOpen(): void {
    this.cancelClose();
    if (this.open() || this.openTimer != null) return;
    this.openTimer = setTimeout(() => {
      this.openTimer = null;
      this.open.set(true);
    }, 120);
  }

  private scheduleClose(): void {
    this.cancelOpen();
    if (!this.open() || this.closeTimer != null) return;
    this.closeTimer = setTimeout(() => {
      this.closeTimer = null;
      this.open.set(false);
    }, 220);
  }

  private cancelOpen(): void {
    if (this.openTimer != null) {
      clearTimeout(this.openTimer);
      this.openTimer = null;
    }
  }

  private cancelClose(): void {
    if (this.closeTimer != null) {
      clearTimeout(this.closeTimer);
      this.closeTimer = null;
    }
  }
}
