import type { RemoteHost } from '../models/remote-host.model';

/**
 * Static host-definition seed (AGT-1921).
 *
 * The Remote-Hosts page is the visible entry point into remote-host management
 * while host definitions remain configuration-owned (see docs/research/
 * remote-ready-kickoff-2026-07.md, Phase 3). RemoteHostsService overlays the
 * real client-registry LastSeen value, so this seed must not claim that an
 * uninstalled runner is online or authenticated.
 *
 * Two execution locations, both in one list so the fleet reads as a single
 * picture (Robert addendum 2026-07-09):
 *   - the operator's local Windows dev machine, whose vitals the backend
 *     exposes for itself, and
 *   - the Hetzner Linux runner (`agent-runner`), initially offline until its
 *     configured client identity reports a real request.
 *
 * Only the local reference entry uses a relative fixture timestamp. Remote
 * liveness always comes from the Task Server identity registry.
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
      clientId: 'local-default',
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
      address: 'agent-runner',
      clientId: 'agent-runner-01',
      status: 'offline',
      os: 'Ubuntu 24.04 LTS',
      lastHeartbeatAt: null,
      uptimeLabel: null,
      capabilities: ['linux', 'git', 'dotnet 10'],
      cliQuotas: [],
      stats: null,
    },
  ];
}
