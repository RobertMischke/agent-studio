/** Orchestrator feature public API. Cycle 9h / ADR-0034. */
export { OrchestratorFeedComponent } from './components/orchestrator-feed/orchestrator-feed';
export { GlobalOrchestratorCardComponent } from './components/global-orchestrator-card/global-orchestrator-card';
export { LoadDistributionComponent } from './components/load-distribution/load-distribution.component';
export { OrchestratorSideSheetComponent } from './components/orchestrator-side-sheet/orchestrator-side-sheet.component';
export { OrchestratorContextHeaderComponent } from './components/orchestrator-context-header/orchestrator-context-header.component';
export { ChatSwitcherRailComponent } from './components/chat-switcher-rail/chat-switcher-rail.component';
export { OrchestratorProjectPickerComponent } from './components/orchestrator-project-picker/orchestrator-project-picker.component';
// AGT-1812: the standalone OrchestratorSettingsModalComponent was retired; its
// content (the platform-global lifecycle flags) now renders as the "Orchestrator"
// section of the consolidated Settings view, so the panel itself is the export.
export { OrchestratorLogicPanelComponent } from './components/orchestrator-logic-panel/orchestrator-logic-panel.component';
export { PromptAdminPanelComponent } from './components/prompt-admin-panel/prompt-admin-panel.component';
export type {
  OrchestratorLogEntry,
  OrchestratorTokenUsage,
  OrchestratorLogResponse,
  GlobalOrchestratorFeedResponse,
  OrchestratorSession,
  OrchestratorSessionResponse,
  OrchestratorContextSession,
  OrchestratorContextSessionsResponse,
  OrchestratorContextDigest,
  OrchestratorContextDigestSource,
  OrchestratorContextDigestSourceName,
  OrchestratorChatTurn,
  OrchestratorChatAttachment,
  OrchestratorChatResponse,
  ChatNavigationContext,
} from './models/orchestrator.model';
export { buildChatNavigationContext } from './chat-navigation-context';
export { buildComposerLocationContext, type ComposerLocationContext } from './composer-location-context';
