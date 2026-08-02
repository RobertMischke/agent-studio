import type { GitGraphCommit } from './git.model';

export interface GitGraphSegment {
  id: string;
  kind: 'incoming' | 'carry' | 'parent' | 'merge';
  lane: number;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  path: string;
}

export interface GitGraphRow {
  commit: GitGraphCommit;
  lane: number;
  nodeX: number;
  nodeY: number;
  width: number;
  height: number;
  segments: GitGraphSegment[];
}

const LANE_STEP = 16;
const X_ORIGIN = 10;
const ROW_HEIGHT = 48;
const ROW_MIDDLE = ROW_HEIGHT / 2;

/**
 * Turns topologically ordered commits into quiet SVG lanes. The algorithm
 * carries parent SHAs between rows, joins merges diagonally, and never assigns
 * lane identity from a mutable label.
 */
export function buildGitGraphRows(commits: readonly GitGraphCommit[]): GitGraphRow[] {
  let lanes: string[] = [];
  return commits.map(commit => {
    const before = [...lanes];
    let lane = before.findIndex(sha => sha === commit.sha);
    if (lane < 0) {
      lane = before.length;
      before.push(commit.sha);
    }

    const parents = [...new Set(commit.parentShas ?? [])];
    const after = [...before];
    after.splice(lane, 1, ...parents);
    const deduped = after.filter((sha, index) => after.indexOf(sha) === index);
    const segments: GitGraphSegment[] = [];

    before.forEach((_, index) => {
      segments.push(segment(`incoming:${index}`, 'incoming', index, index, 0, ROW_MIDDLE));
    });
    before.forEach((sha, index) => {
      if (sha === commit.sha) return;
      const nextIndex = deduped.indexOf(sha);
      if (nextIndex < 0) return;
      segments.push(segment(`carry:${sha}`, 'carry', nextIndex, index, ROW_MIDDLE, ROW_HEIGHT));
    });
    parents.forEach((sha, index) => {
      const nextIndex = deduped.indexOf(sha);
      if (nextIndex < 0) return;
      segments.push(segment(
        `parent:${sha}:${index}`,
        index === 0 ? 'parent' : 'merge',
        nextIndex,
        lane,
        ROW_MIDDLE,
        ROW_HEIGHT,
      ));
    });

    lanes = deduped;
    const laneCount = Math.max(before.length, deduped.length, 1);
    return {
      commit,
      lane,
      nodeX: x(lane),
      nodeY: ROW_MIDDLE,
      width: x(laneCount - 1) + X_ORIGIN,
      height: ROW_HEIGHT,
      segments,
    };
  });
}

function segment(
  id: string,
  kind: GitGraphSegment['kind'],
  lane: number,
  fromLane: number,
  y1: number,
  y2: number,
): GitGraphSegment {
  const x1 = x(fromLane);
  const x2 = x(lane);
  const middleY = y1 + (y2 - y1) / 2;
  const path = x1 === x2
    ? `M ${x1} ${y1} L ${x2} ${y2}`
    : `M ${x1} ${y1} C ${x1} ${middleY}, ${x2} ${middleY}, ${x2} ${y2}`;
  return { id, kind, lane, x1, y1, x2, y2, path };
}

function x(lane: number): number {
  return X_ORIGIN + lane * LANE_STEP;
}
