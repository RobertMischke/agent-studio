# quota

CLI subscription / rate-limit visualisation. Each CLI exposes one or more "windows" (monthly premium for Copilot; 5h+weekly for Codex; rate-limit reset for Claude when over-quota).

## Public API

Imports via `from './features/quota'`. See [`index.ts`](./index.ts).

**Components**:

- `QuotaStripComponent` — compact strip surfacing each installed CLI's quota status; lives at the top of the CLI Usage sidesheet.
- `HeaderQuotaComponent` — donut-ring variant for the status-bar usage hover panel.

**Types**: `QuotaWindow`, `QuotaSnapshot`, `QuotaReport`.

## Notable

- `usedPct` above 100 means the user has overshot the included allotment.
- `GET /api/cli/quota` is cache-only. Each snapshot carries `capturedAt`,
  `ageSeconds`, `stale`, and probe-failure metadata; a failed probe keeps and
  labels the last-good percentages instead of clearing the UI.
- The "↻" buttons force a synchronous re-probe; calls take several seconds because they spawn a fresh PTY.
- Strip vs donut is a deliberate split: the strip is for density when the user wants the full picture; the donut header is for at-a-glance "do I have headroom" in the status bar.
