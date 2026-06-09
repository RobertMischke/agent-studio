/**
 * Public, headless activity-log API for shared chat/projection code.
 *
 * Keep this separate from the full task-detail barrel so pure helpers can use
 * the parser without loading TaskDetailComponent and its standalone imports.
 */
export {
  parseActivityLog,
  buildConversationTurns,
  type ActivityLogGroup,
  type ActivityLogKind,
} from './components/activity-log.parser';
