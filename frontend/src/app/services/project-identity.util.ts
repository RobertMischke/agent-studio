/**
 * Per-project visual identity (initial letter + hue) used wherever a project
 * name appears: board cards, archive rows, task-nav meta, header filter chips,
 * detail-view header. Deterministic — same name always yields the same hue
 * and initial — so a project keeps a stable colour across the UI.
 *
 * The palette has 11 hues spaced around the wheel so 5–10 watched projects
 * can each be told apart at a glance. Saturation/lightness are tuned for
 * the dark Catppuccin-inspired surfaces used elsewhere.
 */

export interface ProjectIdentity {
  initial: string;
  hue: number;
  /** Saturated colour for icon glyph and chip text. */
  color: string;
  /** Translucent ring used as chip border. */
  border: string;
  /** Translucent fill used as chip background. */
  soft: string;
  /** Foreground colour for text that sits on the saturated `color`. */
  onColor: string;
}

const HUES = [220, 270, 305, 340, 16, 36, 60, 95, 145, 175, 200];

function hashName(name: string): number {
  let h = 0;
  for (let i = 0; i < name.length; i++) {
    h = (h * 31 + name.charCodeAt(i)) | 0;
  }
  return Math.abs(h);
}

export function projectIdentity(name: string | null | undefined): ProjectIdentity {
  const trimmed = (name ?? '').trim();
  const alpha = trimmed.replace(/[^A-Za-z0-9]/g, '');
  const initial = (alpha[0] ?? '?').toUpperCase();
  const hue = trimmed ? HUES[hashName(trimmed) % HUES.length] : 220;
  return {
    initial,
    hue,
    color: `hsl(${hue} 78% 72%)`,
    border: `hsla(${hue}, 70%, 65%, 0.55)`,
    soft: `hsla(${hue}, 70%, 60%, 0.16)`,
    onColor: '#0b1020'
  };
}
