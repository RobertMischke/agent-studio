/**
 * Merge two timestamp-keyed item lists (messages + events) in
 * chronological order while preserving the relative input order within
 * each stream. Lives in its own file so the chat component's runtime
 * Angular imports don't drag into the pure vitest spec.
 *
 * Tie-break contract on equal timestamps: primary first, then secondary.
 * The chat surface uses messages as primary so a tool-call event firing
 * alongside an orchestrator turn renders under the turn, not over it.
 */
export function mergeByTimestamp<T extends { timestamp: string }>(
  primary: T[],
  secondary: T[]
): T[] {
  const out: T[] = [];
  let i = 0;
  let j = 0;
  while (i < primary.length && j < secondary.length) {
    if (primary[i].timestamp <= secondary[j].timestamp) {
      out.push(primary[i++]);
    } else {
      out.push(secondary[j++]);
    }
  }
  while (i < primary.length) out.push(primary[i++]);
  while (j < secondary.length) out.push(secondary[j++]);
  return out;
}
