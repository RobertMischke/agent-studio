import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type {
  HostRampStrategy,
  RemoteHost,
} from '../../models/remote-host.model';

export interface RuntimeCapacityChange {
  id: string;
  maxParallelism: number;
  targetLoadPercent: number;
  rampStrategy: HostRampStrategy;
}

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
  readonly capacityChange = output<RuntimeCapacityChange>();
  readonly capacityDraft = signal<number | null>(null);
  readonly targetLoadDraft = signal<number | null>(null);
  readonly rampDraft = signal<HostRampStrategy | null>(null);

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
