import {
  REPAIRED_NOTE_TTL_MS,
  summarizeCliRepairNote,
  type LocalCliHealthSnapshot,
  type LocalCliRepairNote,
} from './cli-repair-note';

describe('summarizeCliRepairNote', () => {
  // Built from local parts so the expected "10:04" holds in any timezone.
  const repairedAt = new Date(2026, 7, 18, 10, 4);
  const now = new Date(2026, 7, 18, 10, 30);

  function note(overrides: Partial<LocalCliRepairNote> = {}): LocalCliRepairNote {
    return {
      cliType: 'claude',
      at: repairedAt.toISOString(),
      repaired: true,
      state: 'Ready',
      message: 'claude CLI repaired (bin shims were missing).',
      versionBefore: '2.1.231',
      versionAfter: '2.1.234',
      ...overrides,
    };
  }

  function snapshot(
    recentRepairs: LocalCliRepairNote[],
    clis: LocalCliHealthSnapshot['clis'] = [],
  ): LocalCliHealthSnapshot {
    return { checkedAt: now.toISOString(), clis, recentRepairs };
  }

  it('returns nothing when the host never repaired anything', () => {
    expect(summarizeCliRepairNote(snapshot([]), now)).toBeNull();
    expect(summarizeCliRepairNote(null, now)).toBeNull();
  });

  it('names the CLI and the local time of a successful repair', () => {
    const item = summarizeCliRepairNote(snapshot([note()]), now);

    expect(item?.label).toBe('claude CLI repaired at 10:04');
    expect(item?.warning).toBe(false);
  });

  it('puts the version change in the tooltip, which is the auto-update evidence', () => {
    const item = summarizeCliRepairNote(snapshot([note()]), now);

    expect(item?.tooltip).toContain('Version 2.1.231 -> 2.1.234.');
    expect(item?.tooltip).toContain('logs/cli-repairs.jsonl');
  });

  it('omits the version change when nothing moved', () => {
    const item = summarizeCliRepairNote(
      snapshot([note({ versionBefore: '2.1.234', versionAfter: '2.1.234' })]),
      now,
    );

    expect(item?.tooltip).not.toContain('->');
  });

  it('fades a successful repair out once it is history', () => {
    const stale = new Date(repairedAt.getTime() + REPAIRED_NOTE_TTL_MS + 1000);

    expect(summarizeCliRepairNote(snapshot([note()]), stale)).toBeNull();
  });

  it('raises a warning when a repair failed', () => {
    const item = summarizeCliRepairNote(
      snapshot(
        [note({ repaired: false, state: 'ShimMissingPackagePresent', message: 'claude CLI repair failed: npm exited 1' })],
        [cli('claude', 'ShimMissingPackagePresent')],
      ),
      now,
    );

    expect(item?.label).toBe('claude CLI repair failed');
    expect(item?.warning).toBe(true);
    expect(item?.tooltip).toContain('Attempted at 10:04.');
  });

  it('keeps a still-broken CLI in front of a newer successful repair elsewhere', () => {
    const item = summarizeCliRepairNote(
      snapshot(
        [
          note({ cliType: 'codex', at: new Date(2026, 7, 18, 10, 20).toISOString() }),
          note({ cliType: 'claude', repaired: false, message: 'claude CLI repair failed: npm exited 1' }),
        ],
        [cli('claude', 'ShimMissingPackagePresent'), cli('codex', 'Ready')],
      ),
      now,
    );

    expect(item?.label).toBe('claude CLI repair failed');
  });

  it('stops warning about a failed repair once that CLI is healthy again', () => {
    const item = summarizeCliRepairNote(
      snapshot(
        [note({ repaired: false, message: 'claude CLI repair failed: npm exited 1' })],
        [cli('claude', 'Ready')],
      ),
      now,
    );

    expect(item).toBeNull();
  });

  it('ignores an unparseable timestamp instead of rendering NaN', () => {
    expect(summarizeCliRepairNote(snapshot([note({ at: 'not-a-date' })]), now)).toBeNull();
  });
});

function cli(cliType: string, state: LocalCliHealthSnapshot['clis'][number]['state']) {
  return {
    cliType,
    packageId: `@example/${cliType}`,
    state,
    action: state === 'Ready' ? 'None' : 'GlobalReinstall',
    summary: `${cliType} summary`,
    available: state === 'Ready',
  };
}
