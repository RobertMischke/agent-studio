import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import {
  WINDOWS_TUNNEL_DEFAULTS,
  windowsTunnelDisplayLabel,
  windowsTunnelDisplayState,
  windowsTunnelLastHealLabel,
} from '../../models/windows-tunnel.model';
import { WindowsTunnelStatusService } from '../../services/windows-tunnel-status.service';

/**
 * Guided registration + live status for the Windows control-plane tunnel
 * keeper and watchdog (AGT-2664). Self-contained: it polls
 * {@link WindowsTunnelStatusService} on its own, so it drops into both the
 * local host's Execution Hosts card and the "Set up agent host" tunnel-mode
 * step without extra parent wiring.
 */
@Component({
  selector: 'app-windows-tunnel-setup',
  standalone: true,
  imports: [],
  templateUrl: './windows-tunnel-setup.html',
  styleUrl: './windows-tunnel-setup.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WindowsTunnelSetupComponent implements OnInit, OnDestroy {
  private readonly tunnel = inject(WindowsTunnelStatusService);

  readonly sshTarget = input(WINDOWS_TUNNEL_DEFAULTS.sshTarget);
  readonly remotePort = input(WINDOWS_TUNNEL_DEFAULTS.remotePort);
  readonly taskServerPort = input(WINDOWS_TUNNEL_DEFAULTS.taskServerPort);

  readonly status = this.tunnel.status;
  readonly loading = this.tunnel.loading;
  readonly statusError = this.tunnel.error;
  readonly state = computed(() => windowsTunnelDisplayState(this.status()));
  readonly stateLabel = computed(() => windowsTunnelDisplayLabel(this.status()));
  readonly lastHealLabel = computed(() => windowsTunnelLastHealLabel(this.status()));
  readonly applicable = computed(() => this.status()?.platform !== 'unsupported');

  readonly registering = signal(false);
  readonly registrationDetail = signal<string | null>(null);
  readonly registrationOk = signal<boolean | null>(null);

  ngOnInit(): void {
    this.tunnel.start();
  }

  ngOnDestroy(): void {
    this.tunnel.stop();
  }

  register(): void {
    if (this.registering()) return;
    this.registering.set(true);
    this.registrationDetail.set(null);
    this.registrationOk.set(null);
    this.tunnel.register({
      sshTarget: this.sshTarget(),
      remotePort: this.remotePort(),
      taskServerPort: this.taskServerPort(),
      intervalMinutes: WINDOWS_TUNNEL_DEFAULTS.intervalMinutes,
      probeIntervalSeconds: WINDOWS_TUNNEL_DEFAULTS.probeIntervalSeconds,
      failureThreshold: WINDOWS_TUNNEL_DEFAULTS.failureThreshold,
    }).subscribe({
      next: response => {
        this.registering.set(false);
        this.registrationOk.set(response.ok);
        this.registrationDetail.set(response.detail);
        if (response.ok) this.tunnel.refresh();
      },
      error: () => {
        this.registering.set(false);
        this.registrationOk.set(false);
        this.registrationDetail.set('The registration request failed before Windows could respond. Check that Studio is running on the Windows control-plane host.');
      },
    });
  }
}
