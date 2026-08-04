import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { RemoteHost } from '../../models/remote-host.model';
import { providerAuthViews } from '../../models/provider-auth.model';

@Component({
  selector: 'app-provider-auth-status',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './provider-auth-status.html',
  styleUrl: './provider-auth-status.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProviderAuthStatusComponent {
  readonly host = input.required<RemoteHost>();
  readonly now = input(Date.now());
  readonly providers = computed(() => providerAuthViews(this.host(), this.now()));

  tooltip(detail: string, expiresAt: string | null, expiresSoon: boolean): string {
    if (!expiresAt) return detail;
    const expiry = new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(expiresAt));
    return `${detail}\nCredential expiry: ${expiry}${expiresSoon ? ' (within 14 days)' : ''}`;
  }
}
