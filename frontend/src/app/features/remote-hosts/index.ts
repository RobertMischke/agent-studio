/** Execution Hosts feature public API (AGT-1921). ADR-0034 barrel. */
export { RemoteHostsPanelComponent } from './components/remote-hosts-panel/remote-hosts-panel';
export { RemoteHostCardComponent } from './components/remote-host-card/remote-host-card';
export { AddHostWizardComponent } from './components/add-host-wizard/add-host-wizard';
export { RunnerSetupDialogComponent } from './components/runner-setup-dialog/runner-setup-dialog';
export { RemoteHostsService } from './services/remote-hosts.service';
export { ProviderAuthStatusService } from './services/provider-auth-status.service';
export * from './models/provider-auth.model';
export { seedRemoteHosts } from './services/remote-hosts.seed';
export {
  boardRemoteSlotsForHost,
  deriveBoardRunningTruth,
  freshHostTelemetry,
  freshRemoteTelemetrySlots,
  latestHostTelemetry,
  RUNNING_TELEMETRY_FRESH_MS,
} from './models/running-truth';
export type { BoardRunningTruth } from './models/running-truth';
export type {
  RemoteHost,
  HostRole,
  HostHeartbeatStatus,
  HostActionKind,
  HostRampStrategy,
  RuntimeCapacitySettings,
  HostCliQuota,
  HostSystemStats,
  HostTelemetryPoint,
  HostTelemetryFinding,
  HostTelemetrySeries,
  HostStatusTone,
  TunnelSupervisionStatus,
  MeterTone,
} from './models/remote-host.model';
export { tunnelSupervisionHealthy } from './models/remote-host.model';
export type { RunnerSetupConfig, RunnerSetupConnectionMode } from './models/runner-setup.model';
export { buildRunnerSetupRequest, runnerSetupIssues } from './models/runner-setup.model';
export {
  formatMemory,
  formatDisk,
  clampPct,
  meterTone,
  hostStatusLabel,
  hostStatusTone,
  hostRoleLabel,
  ramUsedPct,
  diskUsedPct,
  relativeHeartbeat,
} from './models/remote-host.model';
