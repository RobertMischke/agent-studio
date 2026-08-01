import type { GitGraphCommit } from './git.model';

export interface GitGraphSegment {
  id: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

export interface GitGraphRow {
  commit: GitGraphCommit;
  lane: number;
  nodeX: number;
  width: number;
  segments: GitGraphSegment[];
}

const LANE_STEP = 12;
const X_ORIGIN = 8;
const ROW_HEIGHT = 36;

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

    before.forEach((sha, index) => {
      if (sha === commit.sha) return;
      const nextIndex = deduped.indexOf(sha);
      if (nextIndex < 0) return;
      segments.push(segment(`carry:${sha}`, index, 0, nextIndex, ROW_HEIGHT));
    });
    parents.forEach((sha, index) => {
      const nextIndex = deduped.indexOf(sha);
      if (nextIndex < 0) return;
      segments.push(segment(`parent:${sha}:${index}`, lane, ROW_HEIGHT / 2, nextIndex, ROW_HEIGHT));
    });

    lanes = deduped;
    const laneCount = Math.max(before.length, deduped.length, 1);
    return {
      commit,
      lane,
      nodeX: x(lane),
      width: x(laneCount - 1) + X_ORIGIN,
      segments,
    };
  });
}

function segment(id: string, fromLane: number, y1: number, toLane: number, y2: number): GitGraphSegment {
  return { id, x1: x(fromLane), y1, x2: x(toLane), y2 };
}

function x(lane: number): number {
  return X_ORIGIN + lane * LANE_STEP;
}
