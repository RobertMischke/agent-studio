import type { RemoteHost } from '../models/remote-host.model';

/**
 * Static registry seed (AGT-1921, UI-first).
 *
 * The Remote-Hosts page is the visible entry point into remote-host management
 * while the runner-heartbeat host-stats payload and a dedicated backend
 * endpoint are still landing (see docs/research/remote-ready-kickoff-2026-07.md,
 * Phase 3). Per the task card the registry may come statically from
 * configuration for now, so this file stands in for what a
 * `GET /api/hosts` (heartbeat-fed) response will later return. The shapes match
 * {@link RemoteHost} one-for-one, so swapping this constant for an HTTP fetch is
 * a drop-in change with no component churn.
 *
 * Two execution locations, both in one list so the fleet reads as a single
 * picture (Robert addendum 2026-07-09):
 *   - the operator's local Windows dev machine, whose vitals the backend
 *     exposes for itself, and
 *   - the Hetzner Linux runner (`agent-runner`), whose reference vitals today
 *     are 62 GB RAM, up 2d 9h.
 *
 * Timestamps are expressed relative to a passed-in "now" so the seed always
 * reads as freshly heartbeat-ing rather than drifting stale in a fixture.
 */
export function seedRemoteHosts(nowMs: number): RemoteHost[] {
  const iso = (msAgo: number) => new Date(nowMs - msAgo).toISOString();
  const sec = 1000;

  return [
    {
      id: 'local',
      name: 'operator-workstation',
      role: 'local',
      address: null,
      status: 'online',
      os: 'Windows 11 Pro',
      lastHeartbeatAt: iso(8 * sec),
      uptimeLabel: '6h 12m',
      capabilities: ['windows', 'dev-seat', 'git', 'node 22', 'dotnet 10', 'browser'],
      cliQuotas: [
        { cliType: 'claude', plan: 'Max', windowLabel: '5h', usedPct: 41, resetLabel: 'in 2h 40m' },
        { cliType: 'codex', plan: 'Plus', windowLabel: 'weekly', usedPct: 22, resetLabel: 'in 3d' },
      ],
      stats: {
        ramTotalMb: 32 * 1024,
        ramFreeMb: 11 * 1024,
        cpuCores: 16,
        cpuModel: 'AMD Ryzen 7 5800X',
        cpuLoadPct: 37,
        diskTotalGb: 1000,
        diskFreeGb: 412,
      },
    },
    {
      id: 'agent-runner-01',
      name: 'agent-runner-01',
      role: 'remote',
      address: 'ssh://agent@runner.hetzner',
      status: 'online',
      os: 'Ubuntu 24.04 LTS',
      lastHeartbeatAt: iso(11 * sec),
      uptimeLabel: '2d 9h',
      capabilities: ['linux', 'runner 0.5.0', 'git', 'node 22', 'dotnet 10', 'playwright', 'setsid reaper'],
      cliQuotas: [
        { cliType: 'claude', plan: 'Max', windowLabel: '5h', usedPct: 63, resetLabel: 'in 1h 05m' },
        { cliType: 'claude', plan: 'Max', windowLabel: 'weekly', usedPct: 58, resetLabel: 'in 4d' },
        { cliType: 'codex', plan: 'Plus', windowLabel: 'weekly', usedPct: 71, resetLabel: 'in 2d' },
      ],
      stats: {
        ramTotalMb: 62 * 1024,
        ramFreeMb: 38 * 1024,
        cpuCores: 8,
        cpuModel: 'Intel Xeon (Hetzner CX)',
        cpuLoadPct: 54,
        diskTotalGb: 240,
        diskFreeGb: 96,
      },
    },
  ];
}
