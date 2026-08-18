/**
 * Status-bar summary of the backend's local CLI self-heal (AGT-2673).
 *
 * The control-plane host can lose the npm bin shims of a globally installed
 * coding-agent CLI while the package itself stays on disk. The backend now
 * repairs that shape by itself, and this is the surface that keeps the fix
 * from being silent: a quiet note after a successful repair, an acute warning
 * only when a repair failed.
 */

/** Mirrors `AgentStudio.HostHealth.LocalCliInstallState`. */
export type LocalCliInstallState =
  | 'Ready'
  | 'ShimMissingPackagePresent'
  | 'PackageBroken'
  | 'NotInstalled'
  | 'Unknown';

/** Mirrors `AgentStudio.HostHealth.LocalCliHealthEntry`. */
export interface LocalCliHealthEntry {
  cliType: string;
  packageId: string;
  state: LocalCliInstallState;
  action: string;
  summary: string;
  available: boolean;
  version?: string | null;
  packageVersion?: string | null;
  lastRepairAt?: string | null;
  lastRepairSucceeded?: boolean | null;
}

/** Mirrors `AgentStudio.HostHealth.LocalCliRepairNote`. */
export interface LocalCliRepairNote {
  cliType: string;
  at: string;
  repaired: boolean;
  state: LocalCliInstallState;
  message: string;
  versionBefore?: string | null;
  versionAfter?: string | null;
}

/** Mirrors `AgentStudio.HostHealth.LocalCliHealthSnapshot`. */
export interface LocalCliHealthSnapshot {
  checkedAt: string;
  clis: LocalCliHealthEntry[];
  recentRepairs: LocalCliRepairNote[];
}

export interface CliRepairStatusItem {
  label: string;
  tooltip: string;
  /** True only when a repair failed, so the bar stays quiet for the healthy case. */
  warning: boolean;
}

/**
 * How long a successful repair stays on the bar. A repair that worked is
 * history, not an acute state, so it fades out on its own; a repair that
 * failed stays until the CLI is healthy again.
 */
export const REPAIRED_NOTE_TTL_MS = 24 * 60 * 60 * 1000;

/**
 * The one note worth a slot in the status bar, or null when there is nothing
 * to say. Pure so the wording and the fade-out rule are unit-testable without
 * rendering the bar.
 */
export function summarizeCliRepairNote(
  snapshot: LocalCliHealthSnapshot | null,
  now: Date,
): CliRepairStatusItem | null {
  const notes = snapshot?.recentRepairs ?? [];
  if (notes.length === 0) return null;

  // A failed repair outranks a newer successful one for a different CLI: it is
  // the only state here that needs an operator. A failure whose CLI has since
  // become healthy is history and drops out entirely.
  const failed = notes.find(note => !note.repaired && !isHealthy(snapshot, note.cliType));
  if (failed) {
    const at = parse(failed.at);
    if (!at) return null;
    return {
      label: `${failed.cliType} CLI repair failed`,
      tooltip: `${failed.message} Attempted at ${formatLocalTime(at)}. `
        + 'Check the backend log and the workspace logs/cli-repairs.jsonl.',
      warning: true,
    };
  }

  const repaired = notes.find(note => note.repaired);
  if (!repaired) return null;
  const at = parse(repaired.at);
  if (!at) return null;
  if (now.getTime() - at.getTime() > REPAIRED_NOTE_TTL_MS) return null;

  return {
    label: `${repaired.cliType} CLI repaired at ${formatLocalTime(at)}`,
    tooltip: `${repaired.message}${formatVersionChange(repaired)} Recorded in the workspace logs/cli-repairs.jsonl.`,
    warning: false,
  };
}

function parse(value: string): Date | null {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function isHealthy(snapshot: LocalCliHealthSnapshot | null, cliType: string): boolean {
  return (snapshot?.clis ?? []).some(cli => cli.cliType === cliType && cli.state === 'Ready');
}

function formatVersionChange(note: LocalCliRepairNote): string {
  const before = note.versionBefore?.trim();
  const after = note.versionAfter?.trim();
  if (!after || before === after) return '';
  return before ? ` Version ${before} -> ${after}.` : ` Now reporting ${after}.`;
}

/** Local wall-clock `HH:MM`; the operator reads this next to their own clock. */
function formatLocalTime(value: Date): string {
  const hours = `${value.getHours()}`.padStart(2, '0');
  const minutes = `${value.getMinutes()}`.padStart(2, '0');
  return `${hours}:${minutes}`;
}
