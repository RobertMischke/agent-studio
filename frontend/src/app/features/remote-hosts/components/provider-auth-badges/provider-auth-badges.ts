import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { RemoteHost } from '../../models/remote-host.model';
import { providerAuthBadgesForHost, type ProviderAuthBadge } from '../../models/provider-auth.model';

@Component({
  selector: 'app-provider-auth-badges',
  standalone: true,
  imports: [DatePipe, AppTooltipDirective],
  templateUrl: './provider-auth-badges.html',
  styleUrl: './provider-auth-badges.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProviderAuthBadgesComponent {
  readonly host = input.required<RemoteHost>();
  readonly now = input.required<number>();
  readonly codexSignIn = output<{ host: RemoteHost; auth: ProviderAuthBadge }>();
  readonly badges = computed(() => providerAuthBadgesForHost(this.host(), this.now()));

  canSignIn(auth: ProviderAuthBadge): boolean {
    return auth.provider === 'codex'
      && (auth.state === 'unavailable' || auth.state === 'expiring')
      && this.host().role === 'remote'
      && !!this.host().address;
  }

  latestTransition(auth: ProviderAuthBadge) { return auth.history.at(-1) ?? null; }
}
