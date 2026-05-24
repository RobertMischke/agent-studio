/**
 * Pure-function WCAG contrast utilities for theme regression tests.
 *
 * Computes (luminance, contrast ratio) pairs the same way the WCAG 2.1
 * formula does, including alpha-aware foreground folding so a translucent
 * icon glyph on a tinted surface still yields a faithful ratio.
 *
 * Extracted in F40 from `frontend/e2e/system/f37-notification-themes.spec.ts`
 * so the new banner / toast theme specs (F40) can share the same
 * implementation rather than re-deriving the math inline. F37's spec
 * keeps its existing inline copy for now — convert when next touched.
 */

export type Rgba = [number, number, number, number];

/**
 * Parse an `rgb(...)` / `rgba(...)` / `rgb(r g b / a)` colour string into
 * a 4-tuple. Throws on malformed input so a regression caused by an
 * unexpected hex literal slipping into a token surface fails loudly.
 */
export function parseRgb(value: string): Rgba {
  const m = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`Cannot parse colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

/** Relative luminance per WCAG 2.1. Input is an [r, g, b] tuple in [0, 255]. */
export function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * WCAG contrast ratio between a foreground (with alpha) and an opaque
 * background. The foreground is alpha-folded onto the background before
 * the ratio is computed.
 */
export function contrastRatio(fgRaw: string, bgRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const fgRgb: [number, number, number] = [
    Math.round(fg[0] * fg[3] + bg[0] * (1 - fg[3])),
    Math.round(fg[1] * fg[3] + bg[1] * (1 - fg[3])),
    Math.round(fg[2] * fg[3] + bg[2] * (1 - fg[3])),
  ];
  const l1 = luminance(fgRgb);
  const l2 = luminance([bg[0], bg[1], bg[2]]);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}
