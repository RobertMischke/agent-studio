function elapsedLabel(elapsedMs: number): string {
  const total = Math.max(0, Math.floor(elapsedMs / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}h`
    : `${minutes}:${String(seconds).padStart(2, '0')}`;
}

export function lifecyclePhaseLabel(
  phase: string | null | undefined,
  phaseEnteredAt: string | null | undefined,
  steerPendingSince: string | null | undefined,
  nowMs: number,
): string | null {
  if (!phase) return null;
  const labels: Record<string, string> = {
    'human-ready': 'Ready',
    'intake-running': 'Intake Running',
    'intake-blocked': 'Intake Blocked',
    'intake-passed': 'Intake Passed',
    'execution-running': 'Execution Running',
    'post-processing-running': 'Post-Processing',
  };
  if (phase !== 'loop-waiting' && phase !== 'steer-pending') return labels[phase] ?? phase;
  const since = Date.parse((phase === 'steer-pending' ? steerPendingSince : phaseEnteredAt) ?? phaseEnteredAt ?? '');
  const base = phase === 'loop-waiting' ? 'Waiting for loop continuation' : 'Waiting for answer';
  return Number.isFinite(since) ? `${base} ${elapsedLabel(nowMs - since)}` : base;
}
