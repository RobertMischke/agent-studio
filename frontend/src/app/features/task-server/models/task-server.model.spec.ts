import { describe, expect, it } from 'vitest';
import {
  clientKindLabel,
  evidenceStateLabel,
  evidenceStateTone,
  formatBytes,
  formatRelativeTime,
  healthLabel,
  healthTone,
  isLocalUrl,
  managementActionLabel,
  phaseLabel,
} from './task-server.model';

describe('task-server.model helpers', () => {
  describe('formatBytes', () => {
    it('formats each magnitude with binary steps', () => {
      expect(formatBytes(0)).toBe('0 B');
      expect(formatBytes(512)).toBe('512 B');
      expect(formatBytes(1024)).toBe('1.0 KB');
      expect(formatBytes(1024 * 1024)).toBe('1.0 MB');
      expect(formatBytes(2_610_612_736)).toBe('2.4 GB');
      expect(formatBytes(5 * 1024 ** 4)).toBe('5.0 TB');
    });

    it('returns a dash for unknown or negative values', () => {
      expect(formatBytes(null)).toBe('-');
      expect(formatBytes(undefined)).toBe('-');
      expect(formatBytes(NaN)).toBe('-');
      expect(formatBytes(-1)).toBe('-');
    });
  });

  describe('isLocalUrl + phase', () => {
    it('recognises loopback origins', () => {
      expect(isLocalUrl('http://localhost:4010')).toBe(true);
      expect(isLocalUrl('http://127.0.0.1:5030')).toBe(true);
      expect(isLocalUrl('https://tasks.example.com')).toBe(false);
    });
    it('labels phases', () => {
      expect(phaseLabel('local')).toBe('Local');
      expect(phaseLabel('central')).toBe('Central');
    });
  });

  describe('tones (R4: only unreachable / dirty are non-calm)', () => {
    it('maps health to a tone', () => {
      expect(healthTone('healthy')).toBe('ok');
      expect(healthTone('degraded')).toBe('warn');
      expect(healthTone('unreachable')).toBe('error');
      expect(healthLabel('unreachable')).toBe('Unreachable');
    });
    it('maps evidence state to a tone; clean stays calm', () => {
      expect(evidenceStateTone('clean')).toBe('ok');
      expect(evidenceStateTone('dirty')).toBe('warn');
      expect(evidenceStateLabel('dirty')).toBe('Uncommitted changes');
    });
  });

  describe('labels', () => {
    it('labels client kinds and management actions', () => {
      expect(clientKindLabel('agent-instance')).toBe('Agent');
      expect(clientKindLabel('retired')).toBe('Retired');
      expect(managementActionLabel('archive-sweep')).toBe('Archive sweep');
      expect(managementActionLabel('fixture-cleanup')).toBe('Fixture cleanup');
    });
  });

  describe('formatRelativeTime', () => {
    const now = Date.parse('2026-07-11T12:00:00Z');
    it('bins a timestamp into a relative label', () => {
      expect(formatRelativeTime(null, now)).toBe('never');
      expect(formatRelativeTime('2026-07-11T11:59:40Z', now)).toBe('just now');
      expect(formatRelativeTime('2026-07-11T11:30:00Z', now)).toBe('30m ago');
      expect(formatRelativeTime('2026-07-11T09:00:00Z', now)).toBe('3h ago');
      expect(formatRelativeTime('2026-07-09T12:00:00Z', now)).toBe('2d ago');
    });
    it('returns never for an unparseable value', () => {
      expect(formatRelativeTime('not-a-date', now)).toBe('never');
    });
  });
});
