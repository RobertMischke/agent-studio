import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { LocalCliCapabilitySnapshot, RemoteHost } from '../models/remote-host.model';

@Injectable({ providedIn: 'root' })
export class LocalCliCapabilityService {
  readonly snapshot = signal<LocalCliCapabilitySnapshot | null>(null);
  private readonly http = inject(HttpClient, { optional: true });

  refresh(): void {
    if (!this.http) return;
    this.http.get<LocalCliCapabilitySnapshot>('/api/cli/local-capabilities').subscribe({
      next: snapshot => this.snapshot.set(snapshot),
      error: () => {
        // A read failure is not a repair failure. Keep the last durable receipt
        // and do not manufacture an operator alarm from a transport problem.
      },
    });
  }
}

export function projectLocalCliCapabilities(
  hosts: readonly RemoteHost[],
  snapshot: LocalCliCapabilitySnapshot | null,
): RemoteHost[] {
  if (!snapshot) return [...hosts];
  return hosts.map(host => host.role !== 'local' ? host : ({
    ...host,
    capabilities: [
      ...host.capabilities.filter(capability => !capability.startsWith('cli-execution:')),
      ...snapshot.capabilities.map(capability =>
        `cli-execution:${capability.cliType}${capability.version ? ` ${capability.version}` : ''}`),
    ],
    localCliCapabilities: snapshot.capabilities,
    localCliRepair: snapshot.latestRepair,
    localCliRepairAlarm: snapshot.repairAlarm,
    status: snapshot.repairAlarm ? 'degraded' as const : host.status,
  }));
}
