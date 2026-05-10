import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../../../services/update.service';

/**
 * Tiny header chip:  v0.1.0  bd05f36   plus an orange dot when origin/main
 * has moved past stable. Click opens the Update Center drawer. Designed
 * to sit in the top header rail next to the project tabs.
 */
@Component({
  selector: 'app-update-version-badge',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './update-version-badge.component.html',
  styleUrl: './update-version-badge.component.scss'
})
export class UpdateVersionBadgeComponent {
  private readonly client = inject(UpdateClientService);

  readonly productVersion = this.client.productVersion;
  readonly headLocal = this.client.headLocal;
  readonly behindBy = this.client.behindBy;

  readonly tooltip = computed(() => {
    const n = this.behindBy();
    if (n === 0) return `Up to date (v${this.productVersion()} · ${this.headLocal()})`;
    const noun = n === 1 ? 'commit' : 'commits';
    return `${n} ${noun} behind origin/main · click for details`;
  });

  open(): void {
    this.client.openCenter();
  }
}
