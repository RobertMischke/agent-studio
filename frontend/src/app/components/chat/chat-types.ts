/**
 * Shared types for the reusable <app-chat> component.
 *
 * The chat is intentionally presentational: it owns draft text, draft
 * attachments, paste/drop handling, and submit emission, but it does not
 * speak to the backend. Callers feed it a `messages` list and react to
 * `submit`. This keeps it usable for any chat surface (orchestrator side
 * sheet today, per-task chat later).
 */

export type ChatRole = 'user' | 'agent' | 'orchestrator' | 'system';

export interface ChatAttachmentRef {
  /** Display label (alt text). */
  alt: string;
  /**
   * Resolvable URL for rendering. Can be an absolute URL, a project-relative
   * path the host page resolves via an <img> route, or a `blob:` URL for
   * staged-but-not-yet-uploaded files.
   */
  url: string;
  /** True while the file is staged client-side. */
  pending?: boolean;
}

export interface ChatMessage {
  id: string;
  role: ChatRole;
  /** Plain text or Markdown. Agent / orchestrator / system get rendered as Markdown. */
  text: string;
  /** ISO 8601 timestamp. */
  timestamp: string;
  attachments?: ChatAttachmentRef[];
  /**
   * True while the agent reply is streaming or the message is still being
   * persisted. Renders a subtle pulsing indicator.
   */
  pending?: boolean;
  /** When set, the message bubble shows an inline error footer. */
  error?: string;
}

export interface ChatDraftAttachment {
  id: string;
  file: File;
  alt: string;
  /** Object URL for the preview thumbnail. Caller / component is responsible for revoking. */
  previewUrl: string;
}

export interface ChatSubmitEvent {
  text: string;
  attachments: ChatDraftAttachment[];
}

/**
 * Inline event card surfaced inside the chat timeline. The chat is
 * becoming the primary product surface; events from background CLIs
 * (tool calls, watchdog warnings, rate-limit pills, etc.) belong woven
 * into the chronology, not in a separate toast or panel.
 *
 * The chat component is purely presentational: callers feed it an
 * `events` list and the chat merges it with `messages` by timestamp.
 * Persistence and the live data source live elsewhere (Slice D backs
 * this with per-month markdown files + the chat search index; here we
 * pin the rendering contract so a host can already plug in events).
 */
export type ChatEventKind =
  | 'tool-call'
  | 'watchdog'
  | 'rate-limit'
  | 'decision'
  | 'update'
  | 'task';

export interface ChatEvent {
  id: string;
  kind: ChatEventKind;
  /** ISO 8601 timestamp. Used to merge with messages chronologically. */
  timestamp: string;
  /** One-line summary shown in the inline card head. Plain text. */
  summary: string;
  /**
   * Optional expandable detail. Markdown is rendered the same way as
   * agent turns (line-numbered code blocks for >5 lines, inline code,
   * links with safe rels, etc). Leave empty when the summary alone is
   * enough.
   */
  detail?: string;
  /** Higher severity flips the card chrome to the error-coloured palette. */
  severity?: 'info' | 'warn' | 'error';
  /**
   * Optional inline action button rendered next to the timestamp in the
   * card head. The chat surface emits {@link ChatComponent.eventAction}
   * with this event's id so the host can navigate without a page reload
   * (Slice E click-through to the new bug task's detail panel).
   */
  actionLabel?: string;
}
