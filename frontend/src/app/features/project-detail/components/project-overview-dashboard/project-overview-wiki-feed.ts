import type {
  WikiPulse,
  WikiPulseFeedItem,
  WikiRecentEdits,
} from '../../../../models/project-docs.model';

/** Returns the original Pulse when a recent-edits poll has no visible change. */
export function mergeRecentWikiFeed(current: WikiPulse, recent: WikiRecentEdits): WikiPulse {
  const items = recent.edits.map(edit => {
    const existing = current.feed.items.find(item =>
      item.relPath === edit.relPath && item.sha === edit.sha);
    if (existing) return existing;
    const areaSlug = edit.relPath.includes('/') ? edit.relPath.split('/')[0] : null;
    return {
      ...edit,
      areaSlug,
      areaTitle: areaSlug?.replace(/^\d+-/, '') ?? null,
      taskKey: null,
    } satisfies WikiPulseFeedItem;
  });
  const unchanged = current.feed.items.length === items.length
    && current.feed.items.every((item, index) => {
      const next = items[index];
      return item.relPath === next.relPath
        && item.sha === next.sha
        && item.title === next.title
        && item.author === next.author
        && item.authorDateUtc === next.authorDateUtc
        && item.subject === next.subject;
    });
  if (unchanged) return current;
  return {
    ...current,
    generatedAtUtc: new Date().toISOString(),
    feed: {
      available: recent.exists,
      reason: recent.exists || items.length > 0 ? null : 'No docs/ folder for this project yet.',
      items,
    },
  };
}
