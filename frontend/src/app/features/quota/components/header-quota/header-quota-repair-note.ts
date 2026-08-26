import type { CliRepairNotice } from '../../models/quota.model';

export interface CliRepairNote {
  failed: boolean;
  label: string;
  tooltip: string;
}

export function buildCliRepairNote(
  repair: CliRepairNotice | null | undefined,
  cliLabel: (cliType: CliRepairNotice['cliType']) => string,
): CliRepairNote | null {
  if (!repair) return null;
  const failed = repair.status === 'failed';
  const timestamp = Date.parse(repair.completedAt);
  const time = Number.isFinite(timestamp)
    ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(timestamp))
    : 'unknown time';
  const versions = repair.versionBefore || repair.versionAfter
    ? ` Version ${repair.versionBefore ?? 'unknown'} to ${repair.versionAfter ?? 'unknown'}.`
    : '';
  return {
    failed,
    label: failed ? `CLI repair failed at ${time}` : `CLI repaired at ${time}`,
    tooltip: `${cliLabel(repair.cliType)}. ${repair.message ?? ''}${versions}`.trim(),
  };
}
