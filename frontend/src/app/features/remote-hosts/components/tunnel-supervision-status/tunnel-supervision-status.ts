import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TunnelSupervisionService } from '../../services/tunnel-supervision.service';
import { tunnelSupervisionTone } from '../../models/tunnel-supervision.model';

/**
 * Windows control-plane host tunnel keeper/watchdog status (AGT-2664).
 * Hidden entirely on every deployment that has never run the guided
 * `setup-tunnel-supervision.ps1` registration - the overwhelming majority -
 * so this never clutters a Linux-only or single-machine setup.
 */
@Component({
  selector: 'app-tunnel-supervision-status',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './tunnel-supervision-status.html',
  styleUrl: './tunnel-supervision-status.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TunnelSupervisionStatusComponent implements OnInit {
  private readonly service = inject(TunnelSupervisionService);

  readonly overall = computed(() => this.service.response()?.overall ?? 'not-configured');
  readonly snapshot = computed(() => this.service.response()?.snapshot ?? null);
  readonly tone = computed(() => tunnelSupervisionTone(this.overall()));

  ngOnInit(): void {
    this.service.refresh();
  }
}
