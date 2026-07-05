import { Injectable, Signal, WritableSignal, signal } from '@angular/core';

export type ProjectUrlStatus = 'running' | 'offline' | 'unknown';

/**
 * On-demand liveness probe for Project URLs. Keeps this deliberately simple
 * for v1 (per the Project URLs design): a short, throttled `no-cors` HTTP
 * fetch against the URL rather than backend process supervision, so an
 * externally started dev server still reports as running. Each URL gets a
 * cached status signal; reading it via {@link statusFor} triggers a probe
 * when the last one is older than the TTL. Shared by the Explorer tree rows
 * and the Project Hub "Project URLs" panel.
 */
@Injectable({ providedIn: 'root' })
export class ProjectUrlProbeService {
  private readonly states = new Map<string, WritableSignal<ProjectUrlStatus>>();
  private readonly lastProbed = new Map<string, number>();
  private readonly inflight = new Set<string>();

  /** Re-probe no more often than this while a URL stays visible. */
  private readonly ttlMs = 8000;
  /** Abort a probe that hangs (connection accepted but never responds). */
  private readonly timeoutMs = 2500;

  /**
   * Reactive status for a URL. Registers a signal dependency for the caller
   * and kicks off a throttled probe when the cached result is stale. Safe to
   * call from a template — the signal is only written asynchronously once the
   * fetch settles, never synchronously during the read.
   */
  statusFor(url: string): ProjectUrlStatus {
    const sig = this.ensure(url);
    this.maybeProbe(url);
    return sig();
  }

  /** The status signal without triggering a probe (for explicit wiring). */
  signalFor(url: string): Signal<ProjectUrlStatus> {
    return this.ensure(url);
  }

  /** Force an immediate re-probe (e.g. after a restart), bypassing the TTL. */
  refresh(url: string): void {
    void this.probe(url);
  }

  private ensure(url: string): WritableSignal<ProjectUrlStatus> {
    let sig = this.states.get(url);
    if (!sig) {
      sig = signal<ProjectUrlStatus>('unknown');
      this.states.set(url, sig);
    }
    return sig;
  }

  private maybeProbe(url: string): void {
    const last = this.lastProbed.get(url) ?? 0;
    if (Date.now() - last > this.ttlMs) void this.probe(url);
  }

  private async probe(url: string): Promise<void> {
    if (this.inflight.has(url)) return;
    this.inflight.add(url);
    this.lastProbed.set(url, Date.now());
    const sig = this.ensure(url);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      // `no-cors` yields an opaque response but still resolves when the server
      // is reachable and rejects on connection-refused / abort — enough for a
      // running/offline signal without CORS cooperation from the dev server.
      await fetch(url, { mode: 'no-cors', cache: 'no-store', signal: controller.signal });
      sig.set('running');
    } catch {
      sig.set('offline');
    } finally {
      clearTimeout(timer);
      this.inflight.delete(url);
    }
  }
}
