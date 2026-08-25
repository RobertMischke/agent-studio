export interface QuotaFailureIdentity {
  cliType: string;
  cliVersion?: string | null;
  error?: string | null;
  probeFailedAt?: string | null;
}

export function quotaProbeFailureLabel(value: QuotaFailureIdentity): string | null {
  if (!value.error || !value.probeFailedAt) return null;
  const failedAt = Date.parse(value.probeFailedAt);
  if (!Number.isFinite(failedAt)) return null;
  const failureDate = new Date(failedAt);
  const clock = `${String(failureDate.getHours()).padStart(2, '0')}:${String(failureDate.getMinutes()).padStart(2, '0')}`;
  const identity = value.cliVersion ? `${value.cliType} ${value.cliVersion}` : value.cliType;
  return `probe failed ${clock}, ${identity}`;
}
