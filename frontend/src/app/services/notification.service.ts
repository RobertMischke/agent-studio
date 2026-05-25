import { Injectable, signal } from '@angular/core';
import {
  NotificationKind,
  NotificationOptions,
  NotificationState,
} from '../models/app-dialog.model';

const DEFAULT_DURATION: Record<NotificationKind, number> = {
  success: 5000,
  info: 5000,
  warning: 0,
  error: 0,
  accent: 0,
};

/**
 * Stack-based notification surface. Renders styled toasts (success / info /
 * warning / error) via `<app-notification-stack>` mounted at the app shell.
 *
 * Use for non-blocking outcomes ("Lane cleared", "Task deleted", "Settings
 * saved"). For full error overlays with stack trace + copy, keep using
 * `ErrorDialogService.show` — both surfaces share the same visual language.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  readonly notifications = signal<NotificationState[]>([]);

  private nextId = 1;
  private timers = new Map<number, ReturnType<typeof setTimeout>>();

  constructor() {
    // Expose the live instance on window so E2E screenshot specs can
    // drive every kind side-by-side without coupling to a feature flow.
    // No-op outside the browser; harmless in production builds.
    if (typeof window !== 'undefined') {
      (window as unknown as { __notifications?: NotificationService }).__notifications = this;
    }
  }

  notify(options: NotificationOptions): number {
    const id = this.nextId++;
    const hasActions = options.actions && options.actions.length > 0;
    const durationMs = hasActions ? 0 : (options.durationMs ?? DEFAULT_DURATION[options.kind]);
    const state: NotificationState = { id, durationMs, ...options };

    this.notifications.update((arr) => [...arr, state]);
    if (durationMs > 0) {
      const timer = setTimeout(() => this.dismiss(id), durationMs);
      this.timers.set(id, timer);
    }
    return id;
  }

  dismissTopmost(): void {
    const all = this.notifications();
    if (all.length > 0) this.dismiss(all[0].id);
  }

  success(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'success' });
  }

  info(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'info' });
  }

  warning(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'warning' });
  }

  error(message: string, title?: string): number {
    return this.notify({ message, title, kind: 'error' });
  }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(id);
    }
    this.notifications.update((arr) => arr.filter((n) => n.id !== id));
  }

  dismissAll(): void {
    for (const timer of this.timers.values()) clearTimeout(timer);
    this.timers.clear();
    this.notifications.set([]);
  }
}
