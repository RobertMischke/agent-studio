import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import {
  clientKindLabel,
  formatRelativeTime,
  type TaskServerClient,
  type TaskServerClientKind,
} from '../../models/task-server.model';

/**
 * Client-registry list for the Task-Server page (AGT-1924). One row per
 * registered identity: emoji + name, kind badge, relative last-seen, and how
 * many tasks it owns. Presentational - the panel injects the clock so the
 * "last seen" labels tick without a per-row timer.
 *
 * Retired identities render calm (settled history, R4); no status stripe or
 * accent bar (R1). The owned-task counts are per-identity figures shown next to
 * each visible row, so they read as the row's own number, not a rolled-up total.
 */
@Component({
  selector: 'app-task-server-clients-card',
  standalone: true,
  templateUrl: './task-server-clients-card.html',
  styleUrl: './task-server-clients-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskServerClientsCardComponent {
  readonly clients = input.required<readonly TaskServerClient[]>();
  /** Injected clock so relative last-seen labels stay fresh. */
  readonly now = input<number>(Date.now());

  kindLabel(kind: TaskServerClientKind): string { return clientKindLabel(kind); }
  lastSeen(client: TaskServerClient): string { return formatRelativeTime(client.lastSeenAt, this.now()); }
  retired(client: TaskServerClient): boolean { return client.kind === 'retired'; }
}
