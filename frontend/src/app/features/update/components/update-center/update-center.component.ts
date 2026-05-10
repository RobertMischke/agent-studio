import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../../../services/update.service';
import { DevToolsService } from '../../../../services/dev-tools.service';
import { ErrorDialogService } from '../../../../services/error-dialog.service';
import { UpdateHistoryEntry } from '../../../../models/update-service.model';

/**
 * Drawer-style overlay opened from the version badge. Three sections:
 *
 *   1. Identity card — version + commit SHA + behindBy summary.
 *   2. Pending commits list — what's queued on origin/main.
 *   3. Recent runs — last N successful/failed updates from history.
 *
 * In dev mode the bottom of the drawer offers a manual "Update now" trigger.
 * Outside dev mode, the drawer is read-only — pure release-notes feel.
 */
@Component({
  selector: 'app-update-center',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './update-center.component.html',
  styleUrl: './update-center.component.scss'
})
export class UpdateCenterComponent {
  private readonly client = inject(UpdateClientService);
  private readonly devTools = inject(DevToolsService);
  private readonly errors = inject(ErrorDialogService);

  readonly open = this.client.centerOpen;
  readonly status = this.client.status;
  readonly isRunning = this.client.isRunning;
  readonly isDev = computed(() => this.devTools.flags().updateStableEnabled);
  readonly triggerInFlight = signal(false);
  readonly history = signal<UpdateHistoryEntry[]>([]);

  constructor() {
    // Re-load history every time the drawer opens.
    setInterval(() => {
      if (this.open()) this.refreshHistory();
    }, 5_000);
  }

  close(): void {
    this.client.closeCenter();
  }

  async trigger(): Promise<void> {
    if (this.triggerInFlight()) return;
    this.triggerInFlight.set(true);
    try {
      await this.client.trigger('manual via update center', false);
      // The block-modal will own the running UI from here; we keep the
      // drawer open but let the block-modal's z-index sit above it.
      this.refreshHistory();
    } catch (err: any) {
      this.errors.show({
        title: 'Update trigger failed',
        message: err?.message ?? 'Could not reach UpdateService at :5039.',
      });
    } finally {
      this.triggerInFlight.set(false);
    }
  }

  private async refreshHistory(): Promise<void> {
    try {
      const list = await this.client.readHistory(15);
      // newest first
      this.history.set([...list].reverse());
    } catch { /* leave previous list */ }
  }
}
