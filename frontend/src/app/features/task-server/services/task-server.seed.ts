import type { TaskServerStatus } from '../models/task-server.model';
import { isLocalUrl } from '../models/task-server.model';

/**
 * Static Task-Server status seed (AGT-1924), shaped like the future
 * `GET /api/task-server/status` payload. This is the single source the
 * {@link TaskServerService} swaps for an HTTP fetch when the endpoint lands.
 *
 * Only the connected URL is genuinely live: the caller passes the serving
 * origin so the page shows the real URL the SPA is talking to (and derives the
 * local-vs-central phase from it). Everything else is representative sample
 * data; `nowMs` is injected so the relative timestamps stay deterministic in
 * tests (no `Date.now()` inside the seed).
 */
export function seedTaskServerStatus(nowMs: number, origin: string): TaskServerStatus {
  const minsAgo = (m: number) => new Date(nowMs - m * 60_000).toISOString();

  return {
    connection: {
      url: origin,
      phase: isLocalUrl(origin) ? 'local' : 'central',
      health: 'healthy',
      version: '2026.07.0',
      uptimeLabel: '3d 4h',
      authMode: isLocalUrl(origin)
        ? 'Local loopback (X-Client-Id is attribution only)'
        : 'Secure server session',
    },
    store: {
      root: 'C:\\Projects\\agent-taskboard-workspace',
      sizeBytes: 2_610_612_736, // ~2.4 GB
      projectCount: 4,
      taskCount: 128,
      archivedTaskCount: 613,
      identityCount: 3,
    },
    evidence: {
      branch: 'main',
      state: 'clean',
      uncommittedFiles: 0,
      ahead: 0,
      behind: 0,
      lastCommitSha: 'e6112a0',
      lastCommitSubject: 'chore(evidence): commit run artifacts for AGT-1918',
      lastCommitAt: minsAgo(12),
    },
    clients: [
      { id: 'local-default', displayName: 'Local Default', emoji: '\u{1F98A}', kind: 'human', lastSeenAt: minsAgo(1), ownedTaskCount: 96 },
      { id: 'orchestrator', displayName: 'Orchestrator', emoji: '\u{1F916}', kind: 'service', lastSeenAt: minsAgo(3), ownedTaskCount: 27 },
      { id: 'linux-runner-01', displayName: 'linux-runner-01', emoji: '\u{1F4E1}', kind: 'agent-instance', lastSeenAt: minsAgo(240), ownedTaskCount: 5 },
    ],
    recentResults: [],
  };
}
