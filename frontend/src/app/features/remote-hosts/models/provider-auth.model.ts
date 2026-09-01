import type { TaskInfo } from '../../../models/task.model';
import type {
  CapabilityRecoveryEvent,
  RemoteHost,
  RemoteHostCapabilityHealth,
  TaskServerRunnerCapabilitySnapshot,
} from './remote-host.model';

export type ProviderAuthDisplayState = 'ok' | 'retrying' | 'limited' | 'expiring' | 'unavailable' | 'unknown';

export interface ProviderAuthBadge {
  id: string;
  provider: string;
  providerLabel: string;
  runnerId: string;
  hostId: string;
  hostName: string;
  aliases: readonly string[];
  state: ProviderAuthDisplayState;
  detail: string;
  advertisedAt: string | null;
  reachable: boolean;
  expiresAt: string | null;
  expiresSoon: boolean;
  expiryLabel: string | null;
  history: readonly CapabilityRecoveryEvent[];
}

export interface ProviderAuthWaitReason {
  provider: string;
  label: string;
  tooltip: string;
  hostNames: readonly string[];
}

export interface ProviderAuthProvisioningRequest {
  sshTarget: string;
  runnerId: string;
  environmentVariable: 'CLAUDE_CODE_OAUTH_TOKEN' | 'ANTHROPIC_API_KEY';
  secret: string;
}

export interface ProviderAuthProvisioningResponse {
  provider: string;
  environmentVariable: string;
  host: string;
  state: 'awaiting-probe' | 'installed-awaiting-runner';
  detail: string;
  requestedAt: string;
  restartedServices: readonly string[];
  processEnvironmentVerified: boolean;
}

const PROVIDER_AUTH_PREFIX = 'provider-auth:';
const CLI_EXECUTION_PREFIX = 'cli-execution:';
const REACHABLE_RUNNER_MS = 5 * 60_000;
export const PROVIDER_AUTH_EXPIRY_WARNING_MS = 14 * 24 * 60 * 60_000;

export function providerAuthBadgesForSnapshot(
  snapshot: TaskServerRunnerCapabilitySnapshot,
  nowMs: number,
): ProviderAuthBadge[] {
  const providers = providerNames(snapshot.capabilities);
  return providers.map(provider => {
    const capability = snapshot.capabilities.find(item => item.key === `${PROVIDER_AUTH_PREFIX}${provider}`);
    return badgeFromCapability(
      provider,
      capability,
      snapshot.runnerId,
      snapshot.hostId,
      snapshot.name,
      [snapshot.runnerId, snapshot.hostId, snapshot.name],
      snapshot.status === 'active' && isRecent(snapshot.lastSeenAt, nowMs),
      nowMs,
    );
  });
}

export function providerAuthBadgesForHost(host: RemoteHost, nowMs: number): ProviderAuthBadge[] {
  const capabilities = host.capabilityHealth ?? [];
  return providerNames(capabilities).map(provider => badgeFromCapability(
    provider,
    capabilities.find(item => item.key === `${PROVIDER_AUTH_PREFIX}${provider}`),
    host.id,
    host.capacityHostId ?? host.id,
    host.name,
    [host.id, host.clientId, host.capacityHostId ?? '', host.name],
    host.status !== 'offline' && host.status !== 'retired' && isRecent(host.lastHeartbeatAt, nowMs),
    nowMs,
  ));
}

export function providerAuthWaitReason(
  task: TaskInfo,
  statuses: readonly ProviderAuthBadge[],
): ProviderAuthWaitReason | null {
  if (task.state !== '2-ready' || !task.cliType) return null;
  const configuredRunner = task.executionLocation?.configuredRunnerId
    ?? (task.executionLocation?.state === 'queued-remote' ? task.executionLocation.runnerId : null);

  const provider = task.cliType.trim().toLowerCase();
  const candidates = statuses.filter(status =>
    status.provider === provider
    && (!configuredRunner
      || status.aliases.some(alias => alias.toLowerCase() === configuredRunner.toLowerCase())));
  if (candidates.some(status => isUsable(status) && status.reachable)) return null;

  const providerLabel = label(provider);
  const hostNames = [...new Set(candidates.map(status => status.hostName).filter(Boolean))];
  const target = hostNames.length > 0
    ? hostNames.join(', ')
    : configuredRunner ?? 'an execution host';
  const blocked = candidates.filter(status => status.state === 'unavailable' || status.state === 'limited');
  const detail = blocked.length > 0
    ? blocked.map(status => `${status.hostName}: ${status.detail}`).join('\n')
    : candidates.length > 0
      ? candidates.map(status => `${status.hostName}: ${status.detail}`).join('\n')
      : configuredRunner
        ? `No reachable ${configuredRunner} capability snapshot advertises provider-auth:${provider}.`
        : `No reachable runner capability snapshot advertises provider-auth:${provider}.`;
  return {
    provider,
    label: blocked.some(status => status.state === 'limited')
      ? `${providerLabel} rate-limited on ${target}`
      : `Waiting for ${providerLabel} sign-in on ${target}`,
    tooltip: `${detail}\nThe task stays Ready until a fresh provider probe reports an available state.`,
    hostNames: hostNames.length > 0 ? hostNames : configuredRunner ? [configuredRunner] : [],
  };
}

function providerNames(capabilities: readonly RemoteHostCapabilityHealth[]): string[] {
  return [...new Set(capabilities
    .map(capability => {
      if (capability.key.startsWith(PROVIDER_AUTH_PREFIX)) return capability.key.slice(PROVIDER_AUTH_PREFIX.length);
      if (capability.key.startsWith(CLI_EXECUTION_PREFIX)) return capability.key.slice(CLI_EXECUTION_PREFIX.length);
      return '';
    })
    .filter(Boolean))]
    .sort();
}

function badgeFromCapability(
  provider: string,
  capability: RemoteHostCapabilityHealth | undefined,
  runnerId: string,
  hostId: string,
  hostName: string,
  aliases: readonly string[],
  runnerReachable: boolean,
  nowMs: number,
): ProviderAuthBadge {
  const expiresAt = capability?.expiresAt ?? null;
  const expiryMs = expiresAt ? Date.parse(expiresAt) : Number.NaN;
  const expired = Number.isFinite(expiryMs) && expiryMs <= nowMs;
  const expiresSoon = capability?.condition !== 'rate-limited'
    && Number.isFinite(expiryMs)
    && expiryMs > nowMs
    && expiryMs - nowMs <= PROVIDER_AUTH_EXPIRY_WARNING_MS;
  let state: ProviderAuthDisplayState;
  if (!capability || !capability.isFresh || !runnerReachable) state = 'unknown';
  else if (capability.condition === 'rate-limited' || capability.advertisedStatus === 'limited') state = 'limited';
  else if (capability.condition === 'transient-error' || capability.healthState === 'suspect') state = 'retrying';
  else if (expired || capability.advertisedStatus !== 'ready' || capability.healthState === 'draining') state = 'unavailable';
  else if (capability.condition === 'expiring' || expiresSoon) state = 'expiring';
  else state = 'ok';

  const detail = capability
    ? !capability.isFresh
      ? `The last provider probe expired at ${capability.freshUntil}. ${capability.detail ?? capability.reason ?? ''}`.trim()
      : expired
        ? `The advertised provider credential expired at ${expiresAt}.`
        : capability.reason || capability.detail || `provider-auth:${provider} is ${capability.advertisedStatus}.`
    : `No provider-auth:${provider} capability was advertised for this CLI.`;
  return {
    id: `${runnerId}:${provider}`,
    provider,
    providerLabel: label(provider),
    runnerId,
    hostId,
    hostName,
    aliases: aliases.filter(Boolean),
    state,
    detail,
    advertisedAt: capability?.advertisedAt ?? null,
    reachable: runnerReachable && !!capability?.isFresh,
    expiresAt,
    expiresSoon,
    expiryLabel: Number.isFinite(expiryMs) ? expiryDistance(expiryMs - nowMs) : null,
    history: capability?.recoveryHistory ?? [],
  };
}

function isUsable(status: ProviderAuthBadge): boolean {
  return status.state === 'ok' || status.state === 'retrying' || status.state === 'expiring';
}

function isRecent(value: string | null | undefined, nowMs: number): boolean {
  if (!value) return false;
  const observed = Date.parse(value);
  return Number.isFinite(observed) && nowMs - observed <= REACHABLE_RUNNER_MS;
}

function expiryDistance(remainingMs: number): string {
  if (remainingMs <= 0) return 'Expired';
  const days = Math.max(1, Math.ceil(remainingMs / (24 * 60 * 60_000)));
  return `Expires in ${days} ${days === 1 ? 'day' : 'days'}`;
}

function label(provider: string): string {
  if (provider === 'claude') return 'Claude';
  if (provider === 'codex') return 'Codex';
  return provider ? provider[0].toUpperCase() + provider.slice(1) : 'Provider';
}
