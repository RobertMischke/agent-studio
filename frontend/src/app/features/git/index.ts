/** Git feature public API. Cycle 9h / ADR-0034. */
export type {
  GitFileChange,
  GitStatus,
  GitProjectSummary,
  GitHygieneStatus,
  TaskHygieneContext,
  TaskCommitInfo,
  TaskCommitDetail,
  LandedState,
  TaskProvenanceTransition,
  TaskProvenanceMerge,
  TaskLandedLadder,
  TaskCommitMembership,
  TaskProvenanceView,
  TaskProvenanceRecord,
  TaskMergeSignal,
  TaskIntegrationStatus,
  IntegrationStatusValue,
  // Project Hub Git View inventory.
  GitBranchCategory,
  GitWorktreeEntry,
  GitBranchEntry,
  GitCommitEntry,
  GitProjectInventory,
  IntegrationQueueState,
  IntegrationQueueItem,
  PublisherMergeItem,
  PromotionTaskItem,
  PromotionDiffView,
  ProjectIntegrationView,
  // Git-Management cleanup (AGT-2009).
  CleanupTargetKind,
  CleanupMergeStatus,
  CleanupCandidate,
  GitCleanupPlan,
  CleanupExecutionItem,
  CleanupActionOutcome,
  GitCleanupResult,
} from './models/git.model';

// Project Hub Git View tree model (pure builder + node types).
export {
  buildGitTree,
  branchCategoryLabel,
} from './models/git-tree.model';
export type {
  GitTreeGroupId,
  GitTreeGroup,
  GitTreeLeaf,
  GitTreeBranchNode,
  GitTreeWorktreeNode,
  GitTreeCommitNode,
} from './models/git-tree.model';
