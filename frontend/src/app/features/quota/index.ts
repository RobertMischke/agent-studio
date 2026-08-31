/** Quota feature public API. Cycle 9h / ADR-0034. */
export { QuotaApiService } from './services/quota-api.service';
export type { CliModelRouteProfile, CliQuotaWaitPolicy, ProjectCliQuotaWaitPolicy, ModelRoutingRecommendation, ModelRoutingPolicyView } from './services/quota-api.service';
export { QuotaStripComponent } from './components/quota-strip/quota-strip';
export { HeaderQuotaComponent } from './components/header-quota/header-quota';
export type { QuotaWindow, QuotaSnapshot, QuotaReport } from './models/quota.model';
export { quotaProbeFailureDetail, quotaProbeFailureLabel, quotaSnapshotIsStale } from './quota-freshness.util';
