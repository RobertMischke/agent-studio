import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../../../services/update.service';
import { UpdatePhase } from '../../../../models/update-service.model';

/**
 * Full-screen, click-blocking modal that takes over the UI while an update
 * is in flight. Stays alive across an F5 reload because the FE keeps
 * polling the standalone UpdateService on :5039 — even when the main
 * backend is mid-restart, isRunning stays true, the modal stays mounted.
 *
 * ADR-0031: the modal stays mounted across the full 9-phase pipeline
 * (`phase ∈ {preparing, pausing-runners, pulling, building, restarting,
 * verifying-after-restart, resuming, rolling-back}`). Each phase emits a
 * server-side `phaseLabel` (gerund) which we render verbatim; a fallback
 * map covers older Update Service builds that don't ship the label yet.
 *
 * No "cancel" button: an in-flight stable restart must run to completion
 * (see UpdateOrchestrator.RunOrchestrationAsync).
 */
@Component({
  selector: 'app-update-block-modal',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (isRunning()) {
      <div class="upd-block" data-testid="update-block-modal" role="dialog" aria-live="assertive" aria-label="Update in progress">
        <div class="upd-block__panel">
          <div class="upd-block__spinner" aria-hidden="true">
            <span class="upd-block__spinner-ring"></span>
          </div>
          <h2 class="upd-block__title" data-testid="update-block-title">{{ titleLine() }}</h2>
          <p class="upd-block__phase" data-testid="update-block-phase">{{ phaseLine() }}</p>
          @if (status()?.headLocal && status()?.headOrigin) {
            <p class="upd-block__heads">
              <code>{{ status()!.headLocal }}</code>
              <span class="upd-block__arrow">→</span>
              <code>{{ status()!.headOrigin }}</code>
            </p>
          }
          @if (!backendReachable()) {
            <p class="upd-block__hint">Backend is restarting; the app will resume automatically when it is back.</p>
          }
        </div>
      </div>
    }
  `,
  styles: [`
    .upd-block {
      position: fixed;
      inset: 0;
      background: rgba(11, 16, 32, 0.78);
      backdrop-filter: blur(2px);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 500;
      pointer-events: auto;
    }
    .upd-block__panel {
      max-width: 420px;
      width: min(420px, 92vw);
      padding: 1.5rem 1.75rem;
      border-radius: 8px;
      background: #1a1a2e;
      border: 1px solid rgba(137, 180, 250, 0.25);
      color: #cdd6f4;
      text-align: center;
      box-shadow: 0 10px 40px rgba(0, 0, 0, 0.4);
    }
    .upd-block__spinner { width: 38px; height: 38px; margin: 0 auto 0.75rem; display: grid; place-items: center; }
    .upd-block__spinner-ring {
      width: 100%; height: 100%;
      border-radius: 50%;
      border: 3px solid rgba(137, 180, 250, 0.2);
      border-top-color: #89b4fa;
      animation: upd-block-spin 0.9s linear infinite;
    }
    .upd-block__title { margin: 0 0 0.5rem; font-size: 1.05rem; font-weight: 600; }
    .upd-block__phase { margin: 0 0 0.5rem; color: rgba(205, 214, 244, 0.85); }
    .upd-block__heads {
      margin: 0 0 0.25rem;
      font-family: var(--mono-stack, ui-monospace, monospace);
      font-size: 0.8125rem;
      color: rgba(205, 214, 244, 0.7);
    }
    .upd-block__arrow { margin: 0 0.4rem; opacity: 0.6; }
    .upd-block__hint { margin: 0.75rem 0 0; font-size: 0.75rem; color: rgba(205, 214, 244, 0.55); }
    @keyframes upd-block-spin { to { transform: rotate(360deg); } }
  `]
})
export class UpdateBlockModalComponent {
  private readonly client = inject(UpdateClientService);

  readonly isRunning = this.client.isRunning;
  readonly status = this.client.status;

  readonly backendReachable = computed(() => this.status()?.backendReachable ?? false);

  readonly titleLine = computed(() => {
    const s = this.status();
    if (s?.phase === 'rolling-back') return 'Rolling back update';
    return 'Updating Stable';
  });

  readonly phaseLine = computed(() => {
    const s = this.status();
    if (!s) return 'Starting…';
    // Server-supplied label takes precedence (ADR-0031); fall back to a
    // local map so older Update Service builds still render readable text.
    const label = s.phaseLabel ?? humanPhase(s.phase);
    return s.message ? `${label} — ${s.message}` : label;
  });
}

function humanPhase(phase: UpdatePhase | string): string {
  switch (phase) {
    case 'preparing':                 return 'Preparing snapshot';
    case 'pausing-runners':           return 'Pausing runners';
    case 'pulling':                   return 'Pulling and rebuilding';
    case 'building':                  return 'Building';
    case 'restarting':                return 'Restarting backend';
    case 'verifying-after-restart':   return 'Verifying restart';
    case 'resuming':                  return 'Resuming runners';
    case 'rolling-back':              return 'Rolling back';
    case 'done':                      return 'Update verified';
    case 'failed':                    return 'Update failed';
    default:                          return String(phase);
  }
}
