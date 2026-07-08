import type { OrchestratorChatTurn } from '../../../../features/orchestrator';

/**
 * Pure helpers for the orchestrator side sheet. Extracted from the
 * component controller so the component .ts stays within its size budget
 * while the navigation-context / pin logic (MC-2) lives inline where it
 * belongs. These functions carry no Angular dependency and are unit-tested
 * directly.
 */

/**
 * Hide any server user turn that an in-flight local turn already represents.
 *
 * After the operator hits Send we render a local "optimistic" turn so the
 * bubble shows up immediately (including the inline blob preview of any
 * attached image). When the round-trip to the orchestrator finishes the
 * server now reports the same user turn back, but the local turn is still
 * on screen until the persisted attachment URL has been pre-decoded into
 * the browser image cache. Without this dedup, the user would see the
 * bubble briefly duplicate during that pre-decode window.
 *
 * Match strategy: walk local user turns newest-to-oldest and pair each
 * with the newest unmatched server user turn that has the same text and
 * the same number of attachments. Pairing is greedy and one-shot so
 * sending the same message twice in a row only suppresses one copy per
 * local turn.
 */
export function suppressLocalDuplicates(
  server: OrchestratorChatTurn[],
  local: (OrchestratorChatTurn & { localAttachments?: { alt: string; previewUrl: string }[] })[]
): OrchestratorChatTurn[] {
  if (local.length === 0) return server;
  const localUsers = local.filter((t) => t.role === 'user');
  if (localUsers.length === 0) return server;
  const suppress = new Set<string>();
  for (const lt of localUsers) {
    const ltAttCount = lt.localAttachments?.length ?? lt.attachments?.length ?? 0;
    for (let i = server.length - 1; i >= 0; i--) {
      const st = server[i];
      if (suppress.has(st.id)) continue;
      if (st.role !== 'user') continue;
      if ((st.text ?? '') !== (lt.text ?? '')) continue;
      const stAttCount = st.attachments?.length ?? 0;
      if (stAttCount !== ltAttCount) continue;
      suppress.add(st.id);
      break;
    }
  }
  return suppress.size === 0 ? server : server.filter((s) => !suppress.has(s.id));
}

/**
 * Slice E: parse `#tag1 #tag2` patterns at the start of any line in the
 * `/bug` description. A tag word is `[A-Za-z][\w-]*`; a leading `# ` (with
 * a space) is treated as Markdown heading syntax and skipped, so the
 * common case where the user opens the description with a heading does
 * not capture the heading text as a tag.
 */
export function parseBugHashtags(description: string): string[] {
  const found: string[] = [];
  for (const line of description.split('\n')) {
    const trimmed = line.trim();
    if (!/^#[A-Za-z]/.test(trimmed)) continue;
    const matches = trimmed.match(/#[A-Za-z][\w-]*/g);
    if (!matches) continue;
    for (const m of matches) {
      const tag = m.substring(1);
      if (!found.includes(tag)) found.push(tag);
    }
  }
  return found;
}

/**
 * Resolve a persisted chat attachment's relative path to the GET endpoint
 * that actually serves the bytes. Server returns `chat-attachments/<file>`;
 * we strip that prefix and route through the per-project attachments route
 * so the `<img>` in the bubble loads. Returns the input unchanged when the
 * project or path is missing.
 */
export function resolveAttachmentUrl(projectName: string | null, relativePath: string): string {
  if (!projectName || !relativePath) return relativePath;
  const fileName = relativePath.startsWith('chat-attachments/')
    ? relativePath.substring('chat-attachments/'.length)
    : relativePath;
  return `/api/runner/${encodeURIComponent(projectName)}/orchestrator-chat/attachments/${encodeURIComponent(fileName)}`;
}

/**
 * Read a pasted/dropped file as a base64 payload for the multimodal fast
 * path. Strips the `data:<mime>;base64,` prefix so the backend only sees
 * the raw base64. Files larger than 10 MB resolve to null so the inline
 * path is skipped and the chat falls back to the archived-only behaviour
 * (matches the backend upload cap).
 */
export function readFileAsBase64(file: File): Promise<{ base64: string; mimeType: string } | null> {
  return new Promise((resolve) => {
    if (file.size > 10 * 1024 * 1024) {
      resolve(null);
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const result = typeof reader.result === 'string' ? reader.result : '';
      const comma = result.indexOf(',');
      const base64 = comma >= 0 ? result.substring(comma + 1) : result;
      const mimeMatch = /^data:([^;]+);base64,/.exec(result);
      const mimeType = mimeMatch?.[1] ?? file.type ?? 'image/png';
      resolve({ base64, mimeType });
    };
    reader.onerror = () => resolve(null);
    reader.readAsDataURL(file);
  });
}
