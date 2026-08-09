import { Injectable, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel,
} from '@microsoft/signalr';
import type { TaskInfo } from '../models/task.model';
import type { WorkbenchHubEvent } from '../models/project-docs.model';

/**
 * Callbacks the {@link TaskService} registers for the fine-grained task
 * mutation pushes that {@link backend/Hubs/TaskHubBroadcaster.cs} fans out
 * over the `/hubs/jobs` SignalR hub. Every handler is optional; only the ones
 * supplied are wired onto the connection.
 *
 * `reconnected` is the convergence hook — fired after the initial connect AND
 * after every auto-reconnect, so the caller can re-pull the full board to
 * catch anything emitted while the socket was down.
 */
export interface JobsHubHandlers {
  jobCreated?: (info: TaskInfo) => void;
  jobUpdated?: (info: TaskInfo) => void;
  jobMoved?: (e: { id: string; fromState: string; toState: string }) => void;
  jobDeleted?: (e: { id: string; watchPath: string }) => void;
  jobsReordered?: (e: { projectName: string; lane: string | null }) => void;
  jobsBulkChanged?: () => void;
  runnerStatusChanged?: () => void;
  cliStarted?: () => void;
  cliFinished?: () => void;
  reconnected?: () => void;
}

/**
 * Owns the single browser→backend SignalR connection for board-level task
 * mutation events. Push is the primary update path; the {@link TaskService}
 * heartbeat poll is the fallback that still converges if the socket is down.
 *
 * Resilience model:
 *  - `withAutomaticReconnect` retries a *dropped* connection on the documented
 *    back-off schedule. It does NOT cover an initial connect that fails (the
 *    backend was not up yet at app boot) or the case where the back-off
 *    schedule is exhausted, so a cold-retry timer restarts the connection
 *    every {@link COLD_RETRY_MS} until it sticks. The board never blocks on
 *    this — the poll keeps it live in the meantime.
 *  - Every connect/reconnect flips {@link connected} and invokes the
 *    `reconnected` convergence hook.
 */
@Injectable({ providedIn: 'root' })
export class JobsHubClient {
  /** True while the hub socket is up. The poll cadence does not depend on this — it is exposed for diagnostics / tests. */
  readonly connected = signal(false);
  /** Latest Workbench change on the same shared hub connection. */
  readonly workbenchEvent = signal<WorkbenchHubEvent | null>(null);

  private connection: HubConnection | null = null;
  private handlers: JobsHubHandlers | null = null;
  private coldRetryHandle: ReturnType<typeof setTimeout> | null = null;
  private stopped = false;

  // Matches the task spec's auto-reconnect schedule (0s, 2s, 5s, 10s, 30s).
  private static readonly RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000, 30000];
  private static readonly COLD_RETRY_MS = 5000;

  /** Idempotent. Builds the connection, wires handlers, and starts connecting. */
  start(handlers: JobsHubHandlers): void {
    if (this.connection) return;
    if (typeof window === 'undefined') return; // SSR / non-browser: poll-only.

    this.handlers = handlers;
    this.stopped = false;

    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/jobs')
      .withAutomaticReconnect([...JobsHubClient.RECONNECT_DELAYS_MS])
      .configureLogging(LogLevel.Warning)
      .build();

    if (handlers.jobCreated) conn.on('jobCreated', handlers.jobCreated);
    if (handlers.jobUpdated) conn.on('jobUpdated', handlers.jobUpdated);
    if (handlers.jobMoved) conn.on('jobMoved', handlers.jobMoved);
    if (handlers.jobDeleted) conn.on('jobDeleted', handlers.jobDeleted);
    if (handlers.jobsReordered) conn.on('jobsReordered', handlers.jobsReordered);
    if (handlers.jobsBulkChanged) conn.on('jobsBulkChanged', handlers.jobsBulkChanged);
    if (handlers.runnerStatusChanged) conn.on('runnerStatusChanged', handlers.runnerStatusChanged);
    if (handlers.cliStarted) conn.on('cliStarted', handlers.cliStarted);
    if (handlers.cliFinished) conn.on('cliFinished', handlers.cliFinished);
    const workbenchEvent = (event: WorkbenchHubEvent) => this.workbenchEvent.set(event);
    conn.on('workbenchCreated', workbenchEvent);
    conn.on('workbenchUpdated', workbenchEvent);
    conn.on('workbenchDecisionRecorded', workbenchEvent);
    conn.on('workbenchStatusChanged', workbenchEvent);

    conn.onreconnecting(() => this.connected.set(false));
    conn.onreconnected(() => {
      this.connected.set(true);
      this.publishWorkbenchReconnect();
      this.handlers?.reconnected?.();
    });
    // Final close (initial-connect failure or back-off exhausted): keep trying
    // cold so a long backend outage still self-heals once it returns.
    conn.onclose(() => {
      this.connected.set(false);
      this.scheduleColdRetry();
    });

    this.connection = conn;
    this.connect();
  }

  /** Tears down the connection and cancels any pending cold-retry. */
  stop(): void {
    this.stopped = true;
    if (this.coldRetryHandle) {
      clearTimeout(this.coldRetryHandle);
      this.coldRetryHandle = null;
    }
    const conn = this.connection;
    this.connection = null;
    this.connected.set(false);
    if (conn) conn.stop().catch(() => undefined);
  }

  private connect(): void {
    const conn = this.connection;
    if (!conn || this.stopped) return;
    conn
      .start()
      .then(() => {
        this.connected.set(true);
        this.publishWorkbenchReconnect();
        // Initial convergence pull: the board may have changed between the
        // last poll and the socket coming up.
        this.handlers?.reconnected?.();
      })
      .catch(() => {
        // Backend not reachable yet. withAutomaticReconnect does not cover the
        // initial connect, so retry cold. Poll fallback covers the board.
        this.connected.set(false);
        this.scheduleColdRetry();
      });
  }

  private publishWorkbenchReconnect(): void {
    this.workbenchEvent.set({
      type: 'reconnected',
      projectName: null,
      workbenchId: null,
      workbench: null,
      previousStatus: null,
      occurredAtUtc: new Date().toISOString(),
    });
  }

  private scheduleColdRetry(): void {
    if (this.stopped || this.coldRetryHandle || !this.connection) return;
    this.coldRetryHandle = setTimeout(() => {
      this.coldRetryHandle = null;
      this.connect();
    }, JobsHubClient.COLD_RETRY_MS);
  }
}
