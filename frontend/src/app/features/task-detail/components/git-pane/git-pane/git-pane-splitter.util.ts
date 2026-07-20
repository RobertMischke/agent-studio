export const MIN_TREE_PX = 200;
const MIN_DIFF_PX = 320;
const SPLITTER_PX = 1;
const NARROW_TREE_SHARE = 0.39;
const NARROW_DIFF_SHARE = 0.6;

/** Keep both panes usable, scaling their fixed floors only under pressure. */
export function clampTreeWidth(raw: number, containerWidth: number): number {
  if (containerWidth <= 0) return Math.round(Math.max(MIN_TREE_PX, raw));

  const treeFloor = Math.min(MIN_TREE_PX, containerWidth * NARROW_TREE_SHARE);
  const diffFloor = Math.min(MIN_DIFF_PX, containerWidth * NARROW_DIFF_SHARE);
  const upper = Math.max(treeFloor, containerWidth - SPLITTER_PX - diffFloor);
  return Math.round(Math.max(treeFloor, Math.min(upper, raw)));
}
