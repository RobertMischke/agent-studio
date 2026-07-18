import {
  classificationBadges,
  classificationTypeAbbreviation,
  formatAnalyzedDate,
} from './wiki-classification';
import { WikiClassification } from '../../../../models/project-docs.model';

const classification = (overrides: Partial<WikiClassification>): WikiClassification => ({
  status: null,
  supersededBy: null,
  type: null,
  analyzedAt: null,
  ...overrides,
});

describe('wiki-classification badges', () => {
  it('returns no badges for a missing classification', () => {
    expect(classificationBadges(null)).toEqual([]);
    expect(classificationBadges(undefined)).toEqual([]);
  });

  it('renders no status chip for aktuell or unclassified status', () => {
    const aktuell = classificationBadges(classification({ status: 'aktuell', type: 'konzept' }));
    expect(aktuell.map(b => b.key)).toEqual(['type']);
    const none = classificationBadges(classification({ type: 'proposal' }));
    expect(none.map(b => b.key)).toEqual(['type']);
  });

  it('renders a stale chip for veraltet with the analysis date in the tooltip only', () => {
    const badges = classificationBadges(
      classification({ status: 'veraltet', type: 'analyse', analyzedAt: '2026-07-18' }),
    );
    const status = badges.find(b => b.key === 'status')!;
    expect(status.label).toBe('veraltet');
    expect(status.tone).toBe('stale');
    expect(status.tooltip).toContain('Analyse 18.07.2026');
  });

  it('renders a superseded chip for ueberholt naming the successor in the tooltip', () => {
    const badges = classificationBadges(classification({
      status: 'ueberholt',
      supersededBy: 'workbenches/haertung-verteilte-ausfuehrung/historie.html',
      type: 'analyse',
      analyzedAt: '2026-07-18',
    }));
    const status = badges.find(b => b.key === 'status')!;
    expect(status.label).toBe('überholt');
    expect(status.tone).toBe('superseded');
    expect(status.tooltip).toContain('Überholt durch workbenches/haertung-verteilte-ausfuehrung/historie.html');
    expect(status.tooltip).toContain('Analyse 18.07.2026');
  });

  it('renders ueberholt without a successor gracefully', () => {
    const status = classificationBadges(classification({ status: 'ueberholt' }))
      .find(b => b.key === 'status')!;
    expect(status.tooltip).toBe('Überholt.');
  });

  it('renders the type as a muted 2-3-letter code with the full label in the tooltip', () => {
    const badges = classificationBadges(
      classification({ type: 'domain-map', analyzedAt: '2026-07-18' }),
    );
    expect(badges).toEqual([{
      key: 'type',
      label: 'DOM',
      tone: 'muted',
      tooltip: 'Typ: Domain-Map. · Analyse 18.07.2026',
    }]);
  });

  it('orders the status chip before the type code', () => {
    const badges = classificationBadges(
      classification({ status: 'veraltet', type: 'mockup' }),
    );
    expect(badges.map(b => b.key)).toEqual(['status', 'type']);
  });
});

describe('classificationTypeAbbreviation', () => {
  it('maps all agreed types to their compact codes', () => {
    const expected: Record<string, string> = {
      konzept: 'KON', adr: 'ADR', contract: 'CTR', 'domain-map': 'DOM',
      analyse: 'ANA', runbook: 'RUN', workbench: 'WB', mockup: 'MCK',
      proposal: 'PRP', generiert: 'GEN', index: 'IDX',
    };
    for (const [type, abbr] of Object.entries(expected)) {
      expect(classificationTypeAbbreviation(type)).toBe(abbr);
    }
  });

  it('shortens unknown types to three uppercase letters', () => {
    expect(classificationTypeAbbreviation('whitepaper')).toBe('WHI');
  });
});

describe('formatAnalyzedDate', () => {
  it('formats an ISO date as dd.mm.yyyy', () => {
    expect(formatAnalyzedDate('2026-07-18')).toBe('18.07.2026');
  });

  it('falls back to the raw value for non-ISO input', () => {
    expect(formatAnalyzedDate('Juli 2026')).toBe('Juli 2026');
  });
});
