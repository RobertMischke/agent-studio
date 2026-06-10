/** CLI feature public API. Cycle 9h / ADR-0034. */
export { CliAdminPanelComponent } from './components/cli-admin-panel/cli-admin-panel';
export { CliConsoleComponent } from './components/cli-console/cli-console';
export { CliSessionsPanelComponent } from './components/cli-sessions-panel/cli-sessions-panel';
export { CliModelsPanelComponent } from './components/cli-models-panel/cli-models-panel';
export { CliContractsPanelComponent } from './components/cli-contracts-panel/cli-contracts-panel';
export type {
  CliModelInfo,
  CliModelCatalog,
  CliCompletionContract,
  CopilotModelInfo,
  CopilotModelCatalog,
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageSection,
  CliUsageReport,
  LinkedJobRef,
} from './models/cli.model';
export { CLAUDE_FALLBACK_MODEL_ID, MODEL_IDS } from './models/model-ids';
