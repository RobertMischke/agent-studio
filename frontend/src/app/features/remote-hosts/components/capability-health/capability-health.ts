import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type {
  RemoteHost,
  RemoteHostCapabilityHealth,
} from '../../models/remote-host.model';

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

  tone(capability: RemoteHostCapabilityHealth): 'ok' | 'warn' | 'error' | 'idle' {
    if (!capability.isFresh) return 'error';
    switch (capability.healthState) {
      case 'healthy': return 'ok';
      case 'suspect': return 'warn';
      case 'draining': return 'error';
      case 'half-open': return 'idle';
    }
  }
}
