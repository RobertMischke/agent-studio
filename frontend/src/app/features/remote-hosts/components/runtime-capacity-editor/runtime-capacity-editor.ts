import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type {
  HostProjectSlots,
  HostRampStrategy,
  RemoteHost,
} from '../../models/remote-host.model';

export interface RuntimeCapacityChange {
  id: string;
  maxParallelism: number;
  targetLoadPercent: number;
  rampStrategy: HostRampStrategy;
}

/**
 * The host row's capacity section: the slot ledger, the per-project breakdown of
 * who is spending those slots, and the three editable targets (ceiling, target
 * load, ramp).
 *
 * The ledger total is always the central ceiling. It used to be derived from the
 * daemon's reported free slots, which made the total breathe with the claims
 * ("7 active / 1 free", then "2 active / 1 free") and so never described a
 * capacity at all (AGT-2302). Without a ceiling the component says so instead of
 * inventing a total from telemetry.
 */
@Component({
  selector: 'app-runtime-capacity-editor',
  standalone: true,
  imports: [DatePipe, PendingButtonDirective],
  templateUrl: './runtime-capacity-editor.html',
  styleUrl: './runtime-capacity-editor.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RuntimeCapacityEditorComponent {
  readonly host = input.required<RemoteHost>();
  /** Projects occupying this host's slots, derived from the board's lease truth. */
  readonly projectSlots = input<readonly HostProjectSlots[]>([]);
  readonly capacityChange = output<RuntimeCapacityChange>();
  readonly capacityDraft = signal<number | null>(null);
  readonly targetLoadDraft = signal<number | null>(null);
  readonly rampDraft = signal<HostRampStrategy | null>(null);

  /** The hard ceiling, or null when no server has published one yet. */
  readonly ceiling = computed(() => this.host().runtimeCapacity?.maxParallelism ?? null);
  readonly activeSlots = computed(() => Math.max(0, this.host().activeTaskCount ?? 0));
  readonly freeSlots = computed(() => {
    const ceiling = this.ceiling();
    return ceiling === null ? 0 : Math.max(0, ceiling - this.activeSlots());
  });
  readonly slotsLabel = computed(() => {
    const ceiling = this.ceiling();
    return ceiling === null
      ? `${this.activeSlots()} active / capacity not reported`
      : `${this.activeSlots()} active / ${this.freeSlots()} free / ${ceiling} total`;
  });
  /** Slots held by projects the board can name; the rest are unattributed. */
  readonly attributedSlots = computed(() =>
    this.projectSlots().reduce((sum, entry) => sum + entry.activeSlots, 0));

  readonly awaitingAdoption = computed(() => {
    const host = this.host();
    return !!host.runtimeCapacity
      && host.runtimeCapacity.maxParallelism !== host.effectiveMaxParallelism;
  });

  updateCapacityDraft(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.capacityDraft.set(Number.isInteger(value) ? value : null);
  }

  updateTargetLoadDraft(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.targetLoadDraft.set(Number.isInteger(value) ? value : null);
  }

  updateRampDraft(event: Event): void {
    this.rampDraft.set((event.target as HTMLSelectElement).value as HostRampStrategy);
  }

  save(): void {
    const host = this.host();
    const capacity = host.runtimeCapacity;
    if (!capacity || host.busyAction) return;
    const maxParallelism = this.capacityDraft() ?? capacity.maxParallelism;
    const targetLoadPercent = this.targetLoadDraft() ?? capacity.targetLoadPercent;
    const rampStrategy = this.rampDraft() ?? capacity.rampStrategy;
    if (!Number.isInteger(maxParallelism) || maxParallelism < 1 || maxParallelism > 256
      || !Number.isInteger(targetLoadPercent)
      || targetLoadPercent < 50
      || targetLoadPercent > 95) return;
    this.capacityChange.emit({
      id: host.id,
      maxParallelism,
      targetLoadPercent,
      rampStrategy,
    });
    this.capacityDraft.set(null);
    this.targetLoadDraft.set(null);
    this.rampDraft.set(null);
  }
}
