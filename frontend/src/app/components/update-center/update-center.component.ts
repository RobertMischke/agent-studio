import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../services/update.service';
import { DevToolsService } from '../../services/dev-tools.service';
import { ErrorDialogService } from '../../services/error-dialog.service';
import { UpdateHistoryEntry } from '../../models/update-service.model';

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
  template: `
    @if (open()) {
      <div class="upd-center__backdrop" data-testid="update-center-backdrop" (click)="close()"></div>
      <aside class="upd-center" data-testid="update-center" role="dialog" aria-label="Update center">
        <header class="upd-center__head">
          <h2 class="upd-center__title">Update Center</h2>
          <button type="button" class="upd-center__close" data-testid="update-center-close" (click)="close()" aria-label="Close">×</button>
        </header>

        <section class="upd-center__id">
          <div class="upd-center__row">
            <span class="upd-center__label">Version</span>
            <span class="upd-center__value">v{{ status()?.productVersion ?? '—' }}</span>
          </div>
          <div class="upd-center__row">
            <span class="upd-center__label">Local</span>
            <code class="upd-center__sha">{{ status()?.headLocal ?? '—' }}</code>
          </div>
          <div class="upd-center__row">
            <span class="upd-center__label">Origin/main</span>
            <code class="upd-center__sha">{{ status()?.headOrigin ?? '—' }}</code>
          </div>
          <div class="upd-center__row">
            <span class="upd-center__label">Status</span>
            @if ((status()?.behindBy ?? 0) === 0) {
              <span class="upd-center__pill upd-center__pill--ok">Up to date</span>
            } @else {
              <span class="upd-center__pill upd-center__pill--behind">{{ status()!.behindBy }} commit{{ status()!.behindBy === 1 ? '' : 's' }} behind</span>
            }
          </div>
        </section>

        @if ((status()?.pendingCommits?.length ?? 0) > 0) {
          <section class="upd-center__commits">
            <h3 class="upd-center__sub">Pending on origin/main</h3>
            <ul class="upd-center__list" data-testid="update-center-pending">
              @for (c of status()!.pendingCommits; track c.sha) {
                <li class="upd-center__commit">
                  <code>{{ c.sha }}</code>
                  <span class="upd-center__subject">{{ c.subject }}</span>
                </li>
              }
            </ul>
          </section>
        }

        @if (history().length > 0) {
          <section class="upd-center__history">
            <h3 class="upd-center__sub">Recent runs</h3>
            <ul class="upd-center__list" data-testid="update-center-history">
              @for (h of history(); track h.runId) {
                <li class="upd-center__run">
                  <span class="upd-center__pill"
                        [class.upd-center__pill--ok]="h.status === 'ok'"
                        [class.upd-center__pill--fail]="h.status === 'failed'">
                    {{ h.status }}
                  </span>
                  <code>{{ h.headBefore }} → {{ h.headAfter }}</code>
                  <span class="upd-center__dim">{{ h.durationSeconds }}s</span>
                </li>
              }
            </ul>
          </section>
        }

        @if (isDev()) {
          <footer class="upd-center__foot">
            <button
              type="button"
              class="upd-center__btn"
              data-testid="update-center-trigger"
              [disabled]="triggerInFlight() || isRunning()"
              (click)="trigger()">
              @if (triggerInFlight()) { Triggering… }
              @else if (isRunning()) { Update in progress… }
              @else if ((status()?.behindBy ?? 0) > 0) { Update now }
              @else { Re-run update (no changes) }
            </button>
          </footer>
        }
      </aside>
    }
  `,
  styles: [`
    .upd-center__backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.45);
      z-index: 200;
    }
    .upd-center {
      position: fixed;
      top: 0;
      right: 0;
      bottom: 0;
      width: min(420px, 92vw);
      background: #161624;
      color: #cdd6f4;
      border-left: 1px solid rgba(255, 255, 255, 0.08);
      z-index: 201;
      overflow-y: auto;
      padding: 1rem 1.25rem;
      display: flex;
      flex-direction: column;
      gap: 1.25rem;
      font-size: 0.875rem;
    }
    .upd-center__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-bottom: 1px solid rgba(255, 255, 255, 0.08);
      padding-bottom: 0.5rem;
    }
    .upd-center__title { font-size: 1rem; font-weight: 600; margin: 0; }
    .upd-center__close {
      background: transparent;
      border: none;
      color: inherit;
      font-size: 1.5rem;
      cursor: pointer;
      line-height: 1;
    }
    .upd-center__sub {
      font-size: 0.75rem;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: rgba(205, 214, 244, 0.6);
      margin: 0 0 0.5rem;
    }
    .upd-center__row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 0.25rem 0;
    }
    .upd-center__label { color: rgba(205, 214, 244, 0.6); }
    .upd-center__value { font-weight: 600; }
    .upd-center__sha { font-family: var(--mono-stack, ui-monospace, monospace); font-size: 0.8125rem; }
    .upd-center__pill {
      padding: 0.1rem 0.5rem;
      border-radius: 4px;
      font-size: 0.75rem;
      background: rgba(255, 255, 255, 0.06);
    }
    .upd-center__pill--ok      { background: rgba(166, 227, 161, 0.18); color: #a6e3a1; }
    .upd-center__pill--behind  { background: rgba(249, 226, 175, 0.18); color: #f9e2af; }
    .upd-center__pill--fail    { background: rgba(243, 139, 168, 0.20); color: #f38ba8; }
    .upd-center__list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
    }
    .upd-center__commit, .upd-center__run {
      display: grid;
      grid-template-columns: auto 1fr auto;
      gap: 0.5rem;
      align-items: center;
      font-size: 0.8125rem;
    }
    .upd-center__subject {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .upd-center__dim { color: rgba(205, 214, 244, 0.5); }
    .upd-center__foot {
      margin-top: auto;
      padding-top: 0.75rem;
      border-top: 1px solid rgba(255, 255, 255, 0.08);
    }
    .upd-center__btn {
      width: 100%;
      padding: 0.55rem 0.75rem;
      border-radius: 4px;
      border: 1px solid rgba(137, 180, 250, 0.4);
      background: rgba(137, 180, 250, 0.18);
      color: inherit;
      cursor: pointer;
      font-size: 0.875rem;
    }
    .upd-center__btn:hover:not(:disabled) { background: rgba(137, 180, 250, 0.28); }
    .upd-center__btn:disabled { cursor: progress; opacity: 0.6; }
  `]
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
