import type { CliType, TaskInfo } from '../../../models/task.model';
import { cliTypeLabel } from '../../../services/format.util';
import type { RemoteHost, RemoteHostCapabilityHealth } from './remote-host.model';

export type ProviderAuthState = 'ok' | 'unavailable' | 'unknown';

export interface ProviderAuthView {
  provider: CliType;
  label: string;
  state: ProviderAuthState;
  detail: string;
  expiresAt: string | null;
  expiresSoon: boolean;
}

export interface ProviderAuthWaitReason {
  label: string;
  detail: string;
  provider: CliType;
  hosts: readonly string[];
}

const EXPIRY_WARNING_MS = 14 * 24 * 60 * 60_000;

/** Compact provider-auth truth derived only from the runner capability snapshot. */
export function providerAuthViews(host: RemoteHost, now = Date.now()): ProviderAuthView[] {
  const capabilities = host.capabilityHealth ?? [];
  const providers = capabilities
    .map(capabilityProvider)
    .filter((provider): provider is CliType => provider !== null)
    .filter((provider, index, all) => all.indexOf(provider) === index)
    .sort();
  return providers.map(provider => providerAuthView(host, provider, now));
}

export function providerAuthView(
  host: RemoteHost,
  provider: CliType,
  now = Date.now(),
): ProviderAuthView {
  const capability = host.capabilityHealth?.find(
    item => item.key === `provider-auth:${provider}`,
  );
  const state = providerAuthState(capability);
  const expiryMs = capability?.expiresAt ? Date.parse(capability.expiresAt) : Number.NaN;
  const expiresSoon = Number.isFinite(expiryMs)
    && expiryMs > now
    && expiryMs - now <= EXPIRY_WARNING_MS;
  return {
    provider,
    label: cliTypeLabel(provider),
    state,
    detail: capability?.reason
      ?? capability?.detail
      ?? (state === 'unknown'
        ? `No fresh ${cliTypeLabel(provider)} authentication probe is available.`
        : `${cliTypeLabel(provider)} authentication is ${state}.`),
    expiresAt: capability?.expiresAt ?? null,
    expiresSoon,
  };
}

/**
 * A ready remote card must not look inert when every matching runner is held
 * by provider authentication. Missing runner capacity is deliberately left to
 * the existing execution-location surface.
 */
export function providerAuthWaitReason(
  task: TaskInfo,
  hosts: readonly RemoteHost[],
  now = Date.now(),
): ProviderAuthWaitReason | null {
  if (task.state !== '2-ready' || task.executionLocation?.state !== 'queued-remote') return null;
  const provider = task.cliType;
  if (!provider) return null;

  const assigned = task.executionLocation.configuredRunnerId
    ?? task.executionLocation.runnerId
    ?? task.executionLocation.clientId;
  const matching = hosts.filter(host => host.role === 'remote'
    && (!assigned || hostMatches(host, assigned))
    && offersCli(host, provider)
    && host.status !== 'offline'
    && host.status !== 'retired'
    && host.status !== 'draining'
    && host.taskServerConnection?.status !== 'unreachable');
  if (matching.length === 0) return null;
  if (matching.some(host => providerAuthView(host, provider, now).state === 'ok')) return null;

  const names = matching.map(host => host.name);
  const views = matching.map(host => providerAuthView(host, provider, now));
  const unavailable = views.filter(view => view.state === 'unavailable');
  const hostLabel = names.join(', ');
  const action = unavailable.length > 0 ? 'sign-in' : 'authentication status';
  return {
    provider,
    hosts: names,
    label: `Waiting for ${cliTypeLabel(provider)} ${action} on ${hostLabel}`,
    detail: views.map((view, index) => `${names[index]}: ${view.detail}`).join('\n'),
  };
}

function providerAuthState(
  capability: RemoteHostCapabilityHealth | undefined,
): ProviderAuthState {
  if (!capability || !capability.isFresh) return 'unknown';
  if (capability.advertisedStatus !== 'ready') return 'unavailable';
  if (capability.healthState === 'draining' || capability.healthState === 'suspect') {
    return 'unavailable';
  }
  return 'ok';
}

function capabilityProvider(capability: RemoteHostCapabilityHealth): CliType | null {
  const match = /^(?:cli-execution|provider-auth):(claude|codex|gemini)$/.exec(capability.key);
  return match ? match[1] as CliType : null;
}

function offersCli(host: RemoteHost, provider: CliType): boolean {
  const capability = host.capabilityHealth?.find(item => item.key === `cli-execution:${provider}`);
  return capability?.isFresh === true && capability.advertisedStatus === 'ready';
}

function hostMatches(host: RemoteHost, value: string): boolean {
  return [host.id, host.clientId, host.capacityHostId, host.name].some(
    candidate => candidate === value,
  );
}
