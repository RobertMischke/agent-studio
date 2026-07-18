import { WikiClassification } from '../../../../models/project-docs.model';

/** Visual tone of a classification badge in the tree / folder table. */
export type WikiClassBadgeTone = 'stale' | 'superseded' | 'muted';

/** One compact classification badge (status chip or 2-3-letter type code). */
export interface WikiClassBadge {
  key: 'status' | 'type';
  label: string;
  tone: WikiClassBadgeTone;
  tooltip: string;
}

/** 2-3-letter codes for the curated document types shown in the tree. */
const TYPE_ABBREVIATIONS: Readonly<Record<string, string>> = {
  konzept: 'KON',
  adr: 'ADR',
  contract: 'CTR',
  'domain-map': 'DOM',
  analyse: 'ANA',
  runbook: 'RUN',
  workbench: 'WB',
  mockup: 'MCK',
  proposal: 'PRP',
  generiert: 'GEN',
  index: 'IDX',
};

/** Human labels for the curated document types (tooltip copy). */
const TYPE_LABELS: Readonly<Record<string, string>> = {
  konzept: 'Konzept',
  adr: 'ADR',
  contract: 'Contract',
  'domain-map': 'Domain-Map',
  analyse: 'Analyse',
  runbook: 'Runbook',
  workbench: 'Workbench',
  mockup: 'Mockup',
  proposal: 'Proposal',
  generiert: 'Generiert',
  index: 'Index',
};

/** `2026-07-18` -> `18.07.2026` (falls back to the raw value). */
export function formatAnalyzedDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso.trim());
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso.trim();
}

/** 2-3-letter code for a curated type; unknown types shorten to 3 letters. */
export function classificationTypeAbbreviation(type: string): string {
  const clean = type.trim().toLowerCase();
  return TYPE_ABBREVIATIONS[clean] ?? clean.slice(0, 3).toUpperCase();
}

function analyzedSuffix(classification: WikiClassification): string {
  const at = classification.analyzedAt?.trim();
  return at ? ` · Analyse ${formatAnalyzedDate(at)}` : '';
}

/**
 * Compact classification badges for a page row: a muted status chip
 * (`veraltet` / `überholt`; `aktuell` and unclassified render nothing) plus a
 * muted 2-3-letter type code. The analysis date lives in the tooltips only, so
 * the tree keeps its narrow-width budget. Pure - shared by the wiki tree rows
 * and the folder-overview table.
 */
export function classificationBadges(
  classification: WikiClassification | null | undefined,
): WikiClassBadge[] {
  if (!classification) return [];
  const badges: WikiClassBadge[] = [];

  const status = classification.status?.trim().toLowerCase();
  if (status === 'veraltet') {
    badges.push({
      key: 'status',
      label: 'veraltet',
      tone: 'stale',
      tooltip: `Veraltet: Inhalt ist nicht mehr aktuell.${analyzedSuffix(classification)}`,
    });
  } else if (status === 'ueberholt') {
    const successor = classification.supersededBy?.trim();
    badges.push({
      key: 'status',
      label: 'überholt',
      tone: 'superseded',
      tooltip: successor
        ? `Überholt durch ${successor}.${analyzedSuffix(classification)}`
        : `Überholt.${analyzedSuffix(classification)}`,
    });
  }

  const type = classification.type?.trim();
  if (type) {
    const label = TYPE_LABELS[type.toLowerCase()] ?? type;
    badges.push({
      key: 'type',
      label: classificationTypeAbbreviation(type),
      tone: 'muted',
      tooltip: `Typ: ${label}.${analyzedSuffix(classification)}`,
    });
  }

  return badges;
}
