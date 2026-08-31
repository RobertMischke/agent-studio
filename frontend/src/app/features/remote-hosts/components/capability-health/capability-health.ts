import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { taskServerRouteDetail, taskServerRouteStatus } from '../../models/remote-host.model';
import type { RemoteHost, RemoteHostCapabilityHealth } from '../../models/remote-host.model';

@Component({
  selector: 'app-capability-health',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './capability-health.html',
  styleUrl: './capability-health.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CapabilityHealthComponent {
  readonly host = input.required<RemoteHost>();
  readonly routeStatus = computed(() => taskServerRouteStatus(this.host()));
  readonly routeDetail = computed(() => taskServerRouteDetail(this.host()));

  tone(capability: RemoteHostCapabilityHealth): 'ok' | 'warn' | 'error' | 'idle' {
    if (!capability.isFresh) return 'error';
    if (capability.key.startsWith('provider-auth:')) {
      if (capability.operationalState === 'signed-out') return 'error';
      if (capability.operationalState === 'rate-limited'
        || capability.operationalState === 'credentials-expiring') return 'warn';
      if (capability.operationalState === 'transient-auth-error') return 'idle';
    }
    if (capability.advertisedStatus !== 'ready') {
      return capability.advertisedStatus === 'unavailable' ? 'error' : 'warn';
    }
    switch (capability.healthState) {
      case 'healthy': return 'ok';
      case 'suspect': return 'warn';
      case 'draining': return 'error';
      case 'half-open': return 'idle';
    }
  }

  displayState(capability: RemoteHostCapabilityHealth): string {
    if (!capability.isFresh) return 'stale';
    if (capability.key.startsWith('provider-auth:') && capability.operationalState) {
      return capability.operationalState;
    }
    if (capability.advertisedStatus !== 'ready') return capability.advertisedStatus;
    return capability.healthState;
  }
}
