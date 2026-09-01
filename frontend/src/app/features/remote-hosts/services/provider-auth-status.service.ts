import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Observable, filter, map, switchMap, take, tap, timer, timeout } from 'rxjs';
import { NotificationService } from '../../../services/notification.service';
import {
  providerAuthBadgesForSnapshot,
  type ProviderAuthBadge,
  type ProviderAuthProvisioningRequest,
  type ProviderAuthProvisioningResponse,
} from '../models/provider-auth.model';
import type { TaskServerRunnerCapabilitySnapshot } from '../models/remote-host.model';

@Injectable({ providedIn: 'root' })
export class ProviderAuthStatusService implements OnDestroy {
  private static readonly RefreshMs = 60_000;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly notifications = inject(NotificationService);
  private readonly snapshots = signal<readonly TaskServerRunnerCapabilitySnapshot[]>([]);
  private timer: ReturnType<typeof setInterval> | null = null;
  private previous = new Map<string, ProviderAuthBadge>();
  private expiryWarnings = new Set<string>();

  readonly loaded = signal(false);
  readonly statuses = computed(() => this.snapshots()
    .flatMap(snapshot => providerAuthBadgesForSnapshot(snapshot, Date.now())));

  start(): void {
    if (this.timer || !this.http) return;
    this.refresh();
    this.timer = setInterval(() => this.refresh(), ProviderAuthStatusService.RefreshMs);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  ngOnDestroy(): void {
    this.stop();
  }

  refresh(): void {
    if (!this.http) return;
    this.http.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts').subscribe({
      next: snapshots => this.ingest(snapshots ?? []),
      error: () => undefined,
    });
  }

  ingest(snapshots: readonly TaskServerRunnerCapabilitySnapshot[]): void {
    const wasLoaded = this.loaded();
    this.snapshots.set(snapshots);
    const next = new Map(this.statuses().map(status => [status.id, status]));
    if (wasLoaded) {
      for (const [id, current] of next) {
        const prior = this.previous.get(id);
        if (prior?.state !== 'signed-out' && current.state === 'signed-out') {
          this.notifications.error(
            `${current.providerLabel} is genuinely signed out on ${current.hostName}. Ready cards assigned to this host are waiting. ${current.detail}`,
            `${current.providerLabel} sign-in required`,
          );
        } else if (prior?.state !== 'retrying' && current.state === 'retrying') {
          this.notifications.info(
            `${current.providerLabel} reported a transient authentication error on ${current.hostName}. The last usable state is retained and the runner is probing again. ${current.detail}`,
            `${current.providerLabel} transient auth error, retrying`,
          );
        } else if (prior?.state !== 'limited' && current.state === 'limited') {
          this.notifications.info(
            `${current.providerLabel} claims on ${current.hostName} are rate-limited. Matching cards will resume after a successful reset-time probe. ${current.detail}`,
            `${current.providerLabel} rate-limited`,
          );
        } else if (prior && prior.state !== 'ok' && current.state === 'ok') {
          this.notifications.success(
            `${current.providerLabel} authentication recovered on ${current.hostName}. Matching Ready cards are eligible again.`,
            `${current.providerLabel} authentication recovered`,
          );
        }
      }
    }
    for (const current of next.values()) {
      if (current.state !== 'expiring') continue;
      const warningKey = `${current.id}:${current.expiresAt ?? current.detail}`;
      if (this.expiryWarnings.has(warningKey)) continue;
      this.expiryWarnings.add(warningKey);
      this.notifications.info(
        `${current.providerLabel} credentials on ${current.hostName} need attention before they become a hard failure. ${current.detail}`,
        `${current.providerLabel} credentials expiring / re-auth needed`,
      );
    }
    this.previous = next;
    this.loaded.set(true);
  }

  provision(request: ProviderAuthProvisioningRequest): Observable<ProviderAuthProvisioningResponse> {
    if (!this.http) throw new Error('Provider-auth provisioning requires the Studio HTTP client.');
    return this.http.post<ProviderAuthProvisioningResponse>(
      '/api/v1/management/remote-hosts/provider-auth',
      request,
    );
  }

  waitForFreshProbe(
    provider: string,
    aliases: readonly string[],
    baselineAdvertisedAt: string | null,
    timeoutMs = 75_000,
  ): Observable<ProviderAuthBadge> {
    if (!this.http) throw new Error('Provider-auth verification requires the Studio HTTP client.');
    const baseline = baselineAdvertisedAt ? Date.parse(baselineAdvertisedAt) : Number.NEGATIVE_INFINITY;
    const normalizedAliases = new Set(aliases.filter(Boolean).map(alias => alias.toLowerCase()));
    return timer(0, 2_000).pipe(
      switchMap(() => this.http!.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts')),
      tap(snapshots => this.ingest(snapshots ?? [])),
      map(() => this.statuses().find(status =>
        status.provider === provider
        && status.aliases.some(alias => normalizedAliases.has(alias.toLowerCase()))
        && (status.advertisedAt ? Date.parse(status.advertisedAt) > baseline : false))),
      filter((status): status is ProviderAuthBadge => !!status),
      take(1),
      timeout({ first: timeoutMs }),
    );
  }
}
