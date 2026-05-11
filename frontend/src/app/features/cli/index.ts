/** CLI feature public API. Cycle 9h / ADR-0034. */
export { CliAdminPanelComponent } from './components/cli-admin-panel';
export { CliConsoleComponent } from './components/cli-console';
export { CliSessionsPanelComponent } from './components/cli-sessions-panel';
export { CliUsageSheetComponent } from './components/cli-usage-sheet';
export type {
  CliModelInfo,
  CliModelCatalog,
  CopilotModelInfo,
  CopilotModelCatalog,
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageSection,
  CliUsageReport,
} from './models/cli.model';
