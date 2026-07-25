export const EMPTY_STATE_COLS = 60;
export const EMPTY_STATE_ROWS = 24;
export const EMPTY_STATE_CELL = 8;
export const EMPTY_STATE_GAP = 2;
export const EMPTY_STATE_STEP_MS = 115;
export const EMPTY_STATE_FRAME_MS = 34;

export type EmptyStatePhase = 'chaos' | 'forming' | 'smiley' | 'decay';

const FORM_START = 3_000;
const SMILEY_START = 5_400;
const DECAY_START = 7_400;
const CHAOS_RETURN = 9_900;
export const EMPTY_STATE_CYCLE_MS = 12_000;

export interface EmptyStateFrame {
  phase: EmptyStatePhase;
  progress: number;
}

export function emptyStateFrame(elapsedMs: number): EmptyStateFrame {
  const elapsed = ((elapsedMs % EMPTY_STATE_CYCLE_MS) + EMPTY_STATE_CYCLE_MS) % EMPTY_STATE_CYCLE_MS;
  if (elapsed < FORM_START || elapsed >= CHAOS_RETURN) {
    return { phase: 'chaos', progress: elapsed < FORM_START ? elapsed / FORM_START : 1 };
  }
  if (elapsed < SMILEY_START) {
    return { phase: 'forming', progress: (elapsed - FORM_START) / (SMILEY_START - FORM_START) };
  }
  if (elapsed < DECAY_START) {
    return { phase: 'smiley', progress: 1 };
  }
  return { phase: 'decay', progress: (elapsed - DECAY_START) / (CHAOS_RETURN - DECAY_START) };
}

export function createSmileyMask(): Uint8Array {
  const mask = new Uint8Array(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
  for (const centreX of [10, 30, 50]) drawFace(mask, centreX, 11.5);
  return mask;
}

export function cellOrder(index: number): number {
  let value = (index + 1) * 2_654_435_761;
  value = Math.imul(value ^ (value >>> 15), 2_246_822_519);
  value = Math.imul(value ^ (value >>> 13), 3_266_489_917);
  return ((value ^ (value >>> 16)) >>> 0) / 4_294_967_295;
}

function drawFace(mask: Uint8Array, centreX: number, centreY: number): void {
  const radius = 8.4;
  for (let y = 0; y < EMPTY_STATE_ROWS; y++) {
    for (let x = 0; x < EMPTY_STATE_COLS; x++) {
      const distance = Math.hypot(x - centreX, y - centreY);
      if (Math.abs(distance - radius) < 0.72) set(mask, x, y);
    }
  }

  for (const eyeX of [centreX - 3, centreX + 3]) {
    set(mask, eyeX, centreY - 2.5);
    set(mask, eyeX, centreY - 1.5);
  }

  const smile = [
    [-4, 2], [-3, 3], [-2, 4], [-1, 5], [0, 5],
    [1, 5], [2, 4], [3, 3], [4, 2],
  ] as const;
  for (const [dx, dy] of smile) set(mask, centreX + dx, centreY + dy);
}

function set(mask: Uint8Array, x: number, y: number): void {
  const cellX = Math.round(x);
  const cellY = Math.round(y);
  if (cellX < 0 || cellX >= EMPTY_STATE_COLS || cellY < 0 || cellY >= EMPTY_STATE_ROWS) return;
  mask[cellY * EMPTY_STATE_COLS + cellX] = 1;
}
