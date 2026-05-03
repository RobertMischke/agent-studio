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
