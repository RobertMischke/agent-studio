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
  template: `
    <button
      type="button"
      class="version-badge"
      [class.version-badge--behind]="behindBy() > 0"
      data-testid="version-badge"
      [attr.title]="tooltip()"
      (click)="open()">
      <span class="version-badge__v">v{{ productVersion() }}</span>
      <span class="version-badge__sha">{{ headLocal() }}</span>
      @if (behindBy() > 0) {
        <span class="version-badge__dot" data-testid="version-badge-dot" aria-hidden="true"></span>
      }
    </button>
  `,
  styles: [`
    .version-badge {
      display: inline-flex;
      align-items: center;
      gap: 0.4rem;
      padding: 0.2rem 0.55rem;
      border-radius: 4px;
      border: 1px solid rgba(255, 255, 255, 0.12);
      background: rgba(255, 255, 255, 0.04);
      color: rgba(205, 214, 244, 0.85);
      font-size: 0.75rem;
      font-family: var(--mono-stack, ui-monospace, SFMono-Regular, monospace);
      line-height: 1;
      cursor: pointer;
      position: relative;
    }
    .version-badge:hover {
      background: rgba(255, 255, 255, 0.08);
    }
    .version-badge--behind {
      border-color: rgba(249, 226, 175, 0.45);
    }
    .version-badge__sha {
      opacity: 0.6;
    }
    .version-badge__dot {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: #f9e2af;
      box-shadow: 0 0 6px rgba(249, 226, 175, 0.8);
    }
  `]
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
