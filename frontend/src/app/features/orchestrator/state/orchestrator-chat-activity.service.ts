import { Injectable, signal } from '@angular/core';
import type { OrchestratorContextSession } from '../models/orchestrator.model';

/**
 * Browser-local overlay for turns that have been submitted through the
 * side-sheet transport but have not completed yet. The managed context list
 * remains authoritative for server-side active and queued turns; this overlay
 * closes the visibility gap before its next refresh arrives.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorChatActivityService {
  private readonly _pendingContextKeys = signal<ReadonlySet<string>>(new Set());

  readonly pendingContextKeys = this._pendingContextKeys.asReadonly();

  start(contextKey: string): void {
    this._pendingContextKeys.update(current => {
      if (current.has(contextKey)) return current;
      return new Set([...current, contextKey]);
    });
  }

  finish(contextKey: string): void {
    this._pendingContextKeys.update(current => {
      if (!current.has(contextKey)) return current;
      const next = new Set(current);
      next.delete(contextKey);
      return next;
    });
  }

  isWorking(context: Pick<OrchestratorContextSession, 'contextKey' | 'runtimeStatus'>): boolean {
    return context.runtimeStatus === 'active'
      || context.runtimeStatus === 'queued'
      || this._pendingContextKeys().has(context.contextKey);
  }

  workingContextKeys(sessions: readonly OrchestratorContextSession[]): ReadonlySet<string> {
    const keys = new Set(this._pendingContextKeys());
    for (const session of sessions) {
      if (session.runtimeStatus === 'active' || session.runtimeStatus === 'queued') {
        keys.add(session.contextKey);
      }
    }
    return keys;
  }
}
