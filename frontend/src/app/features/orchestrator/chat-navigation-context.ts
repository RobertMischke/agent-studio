import type { ChatNavigationContext } from './models/orchestrator.model';
import type { PageContext } from '../../models/page-context.model';
import { pageContextKey } from '../../models/page-context.model';

/**
 * Pure inputs that describe "where the operator is" when they hit send.
 * Kept narrow so the builder is testable without dragging the side-sheet
 * component into the test harness.
 */
export interface ChatNavigationContextInput {
  activeJobId: string | null;
  activeTaskKey?: string | null;
  activeJobTitle: string | null;
  activeJobState?: string | null;
  laneFilter?: string | null;
  observedSurface?: string | null;
  affectedComponent?: string | null;
  pageContext?: PageContext | null;
  /**
   * Override for tests. Production should leave this undefined so the
   * builder stamps the real wall-clock UTC ISO string at call time.
   */
  now?: () => Date;
}

/**
 * Build the `navigationContext` block that ships with every project-chat
 * POST. The contract is intentionally minimal: callers feed in router /
 * detail-panel state, this function decides which `currentPage` token
 * applies and emits a JSON-shaped object with absent fields omitted (so the
 * backend's "no nav context" detection stays clean).
 *
 * Rules:
 * - A non-empty `activeJobId` means the operator is on `task-detail`.
 * - Otherwise the page is `kanban-board` (default surface).
 * - `currentLaneFilter` is forwarded when the operator has filtered the
 *   board; it disambiguates "what's on this page" when no task is open.
 */
export function buildChatNavigationContext(
  input: ChatNavigationContextInput
): ChatNavigationContext {
  const out: ChatNavigationContext = {};
  const taskId = sanitize(input.activeJobId);
  const taskKey = sanitize(input.activeTaskKey ?? null);
  const taskTitle = sanitize(input.activeJobTitle);
  const taskState = sanitize(input.activeJobState ?? null);
  const lane = sanitize(input.laneFilter ?? null);
  const surface = sanitize(input.observedSurface ?? null);
  const component = sanitize(input.affectedComponent ?? null);
  const page = input.pageContext ?? null;

  out.currentPage = page ? 'repository-page' : taskKey || taskId ? 'task-detail' : 'kanban-board';
  if (!page && taskId) out.currentTaskId = taskId;
  if (!page && taskKey) out.currentTaskKey = taskKey;
  if (!page && taskTitle) out.currentTaskTitle = taskTitle;
  if (!page && taskState) out.currentTaskState = taskState;
  if (lane) out.currentLaneFilter = lane;
  out.observedSurface = surface ?? 'Agent Studio Orchestrator chat';
  if (component) out.affectedComponent = component;
  if (page) {
    out.pageRef = pageContextKey(page);
    out.pageTitle = page.title;
    out.pageType = page.pageType;
    out.pageExcerpt = page.excerpt;
  }

  const now = (input.now ?? (() => new Date()))();
  out.viewportTimestamp = now.toISOString();
  return out;
}

function sanitize(value: string | null | undefined): string | null {
  if (value == null) return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}
