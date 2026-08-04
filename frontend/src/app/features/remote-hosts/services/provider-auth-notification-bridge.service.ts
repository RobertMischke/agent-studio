import { effect, inject, Injectable } from '@angular/core';
import { NotificationService } from '../../../services/notification.service';
import { providerAuthViews } from '../models/provider-auth.model';
import type { RemoteHostCapabilityHealth } from '../models/remote-host.model';
import { RemoteHostsService } from './remote-hosts.service';

const HANDLED_KEY = 'atp.providerAuth.handled';
const HISTORY_FRESH_MS = 5 * 60_000;
const HANDLED_MAX = 100;

/** Turns runner-auth status changes into one actionable Studio notification. */
@Injectable({ providedIn: 'root' })
export class ProviderAuthNotificationBridge {
  private readonly remoteHosts = inject(RemoteHostsService);
  private readonly notifications = inject(NotificationService);
  private readonly previous = new Map<string, string>();
  private readonly handled = loadHandled();

  constructor() {
    effect(() => {
      const now = Date.now();
      for (const host of this.remoteHosts.hosts()) {
        for (const auth of providerAuthViews(host, now)) {
          const key = `${host.id}:${auth.provider}`;
          const previous = this.previous.get(key);
          const capability = host.capabilityHealth?.find(
            item => item.key === `provider-auth:${auth.provider}`,
          );
          const recentLoss = recentUnavailableTransition(capability, now);
          if (auth.state === 'unavailable' && (previous === 'ok' || recentLoss)) {
            const eventKey = recentLoss
              ? `loss:${key}:${recentLoss.occurredAt}`
              : `loss:${key}:${capability?.advertisedAt ?? now}`;
            this.once(eventKey, () => this.notifications.notify({
              kind: 'error',
              title: `${auth.label} sign-in required`,
              message: `${host.name} can no longer run ${auth.label} tasks. Ready cards now show this wait reason.`,
              details: [auth.detail],
              durationMs: 0,
            }));
          }
          if (auth.expiresSoon && auth.expiresAt) {
            this.once(`expiry:${key}:${auth.expiresAt}`, () => this.notifications.notify({
              kind: 'warning',
              title: `${auth.label} credential expires soon`,
              message: `Renew ${auth.label} authentication on ${host.name} before ${formatExpiry(auth.expiresAt!)}.`,
              details: [auth.detail],
              durationMs: 0,
            }));
          }
          this.previous.set(key, auth.state);
        }
      }
    });
  }

  private once(key: string, action: () => void): void {
    if (this.handled.has(key)) return;
    this.handled.add(key);
    action();
    persistHandled(this.handled);
  }
}

function recentUnavailableTransition(
  capability: RemoteHostCapabilityHealth | undefined,
  now: number,
): NonNullable<RemoteHostCapabilityHealth['statusHistory']>[number] | null {
  return [...(capability?.statusHistory ?? [])]
    .reverse()
    .find(event => event.fromStatus === 'ready'
      && event.toStatus === 'unavailable'
      && now - Date.parse(event.occurredAt) <= HISTORY_FRESH_MS) ?? null;
}

function formatExpiry(value: string): string {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(value));
}

function loadHandled(): Set<string> {
  try {
    if (typeof localStorage === 'undefined') return new Set();
    const parsed: unknown = JSON.parse(localStorage.getItem(HANDLED_KEY) ?? '[]');
    return new Set(Array.isArray(parsed) ? parsed.filter(item => typeof item === 'string') : []);
  } catch {
    return new Set();
  }
}

function persistHandled(handled: Set<string>): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(HANDLED_KEY, JSON.stringify([...handled].slice(-HANDLED_MAX)));
  } catch {
    // Browser storage is optional. The in-memory gate still prevents a toast loop.
  }
}
