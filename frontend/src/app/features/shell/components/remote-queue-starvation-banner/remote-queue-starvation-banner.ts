import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input } from '@angular/core';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import { RemoteQueueStarvationService } from '../../services/remote-queue-starvation.service';

@Component({
  selector: 'app-remote-queue-starvation-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './remote-queue-starvation-banner.html',
  styleUrl: './remote-queue-starvation-banner.scss',
})
export class RemoteQueueStarvationBannerComponent implements OnInit, OnDestroy {
  private readonly starvation = inject(RemoteQueueStarvationService);
  private detach: (() => void) | null = null;

  readonly projects = input<readonly string[]>([]);
  readonly snapshot = this.starvation.snapshot;
  readonly visibleItems = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot?.active) return [];
    const projects = this.projects();
    const items = projects.length === 0
      ? snapshot.items
      : (() => {
          const visible = new Set(projects.map(project => project.toLowerCase()));
          return snapshot.items.filter(item => visible.has(item.projectName.toLowerCase()));
        })();
    // Gate-blocked cards have their own banner with the one action that helps
    // (validate the profile). Counting them here too would report the same
    // starvation twice with the less useful of the two explanations.
    return items.filter(item => !item.buildProfileGateBlocked);
  });
  readonly availableSlots = computed(() => this.snapshot()?.availableSlots ?? 0);
  readonly thresholdMinutes = computed(() => this.snapshot()?.thresholdMinutes ?? 0);
  readonly hasRejections = computed(() =>
    this.visibleItems().some(item => item.lastRejection != null));

  ngOnInit(): void {
    this.detach = this.starvation.attach();
  }

  ngOnDestroy(): void {
    this.detach?.();
    this.detach = null;
  }
}
