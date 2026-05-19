/** CLI feature public API. Cycle 9h / ADR-0034. */
export { CliAdminPanelComponent } from './components/cli-admin-panel/cli-admin-panel';
export { CliConsoleComponent } from './components/cli-console/cli-console';
export { CliSessionsPanelComponent } from './components/cli-sessions-panel/cli-sessions-panel';
export { CliUsageSheetComponent } from './components/cli-usage-sheet/cli-usage-sheet';
export type {
  CliModelInfo,
  CliModelCatalog,
  CopilotModelInfo,
  CopilotModelCatalog,
  CliSessionInfo,
  CliUsageProjectGroup,
  CliUsageSection,
  CliUsageReport,
  LinkedJobRef,
} from './models/cli.model';
