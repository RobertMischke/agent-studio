import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ConnectionStatusService } from '../../services/connection-status.service';

/**
 * App-wide "backend offline" banner. Renders a prominent, fixed strip at the
 * top of the viewport whenever {@link ConnectionStatusService.offline} is true
 * (the SignalR socket has been down past the grace window). It self-hides the
 * moment the connection returns.
 *
 * Already-loaded data stays on screen behind the banner — the warning's job is
 * to make "this might be stale, the backend is unreachable" immediately
 * obvious, not to blank the UI. Styled from the semantic notify-error tokens so
 * it tracks light/dark themes.
 */
@Component({
  selector: 'app-offline-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (conn.offline()) {
      <div class="offline-banner" role="alert" aria-live="assertive" data-testid="offline-banner">
        <span class="offline-banner__dot" aria-hidden="true"></span>
        <span class="offline-banner__text">
          <strong>Backend not reachable.</strong>
          The connection is down — data shown may be stale and actions are blocked until the
          connection returns. Reconnecting automatically…
        </span>
      </div>
    }
  `,
  styles: [`
    .offline-banner {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      z-index: 4000;
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 7px 16px;
      font-size: 13px;
      line-height: 1.4;
      color: var(--notify-error-icon-fg, #f87171);
      background: var(--notify-error-tint, rgba(248, 113, 113, 0.12));
      border-bottom: 2px solid var(--notify-error-border, rgba(248, 113, 113, 0.45));
      backdrop-filter: blur(4px);
      box-shadow: 0 2px 10px rgba(0, 0, 0, 0.18);
      animation: offline-banner-in 140ms ease-out;
    }
    .offline-banner__text { color: var(--studio-text, inherit); }
    .offline-banner__text strong { color: var(--notify-error-icon-fg, #f87171); }
    .offline-banner__dot {
      flex: 0 0 auto;
      width: 9px;
      height: 9px;
      border-radius: 50%;
      background: var(--notify-error-icon-fg, #f87171);
      box-shadow: 0 0 0 0 var(--notify-error-icon-fg, #f87171);
      animation: offline-banner-pulse 1.6s ease-out infinite;
    }
    @keyframes offline-banner-in {
      from { transform: translateY(-100%); opacity: 0; }
      to   { transform: translateY(0); opacity: 1; }
    }
    @keyframes offline-banner-pulse {
      0%   { box-shadow: 0 0 0 0 var(--notify-error-border, rgba(248, 113, 113, 0.45)); }
      70%  { box-shadow: 0 0 0 6px transparent; }
      100% { box-shadow: 0 0 0 0 transparent; }
    }
    @media (prefers-reduced-motion: reduce) {
      .offline-banner { animation: none; }
      .offline-banner__dot { animation: none; }
    }
  `],
})
export class OfflineBannerComponent {
  readonly conn = inject(ConnectionStatusService);
}
