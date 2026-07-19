import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { TaskService } from '../../../../services/task.service';
import { CrashRecoveryPromptComponent } from './crash-recovery-prompt.component';

describe('CrashRecoveryPromptComponent', () => {
  it('compiles and loads pending crash recovery items', async () => {
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getPendingCrashRecoveries: () => of({ pending: [] }),
            commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
            dismissCrashRecovery: () => of({ status: 'dismissed', pending: null, commitSha: null, error: null }),
          },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();

    expect(fixture.componentInstance).toBeTruthy();
    expect(fixture.componentInstance.pending()).toEqual([]);
  });

  it('leave-all dismisses every pending item in sequence and closes the dialog', async () => {
    const items = [
      { id: 'a', projectName: 'P1', jobId: null, reason: 'r', repoRoot: 'x', message: 'm', files: [], createdAt: '2026-07-18T00:00:00Z' },
      { id: 'b', projectName: 'P2', jobId: null, reason: 'r', repoRoot: 'y', message: 'm', files: [], createdAt: '2026-07-18T00:00:00Z' },
    ];
    const dismissed = vi.fn(() => of({ status: 'dismissed', pending: null, commitSha: null, error: null }));
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getPendingCrashRecoveries: () => of({ pending: items }),
            commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
            dismissCrashRecovery: dismissed,
          },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    // app-dialog renders into an overlay on document.body, not under the fixture.
    const button = document.querySelector<HTMLButtonElement>('[data-testid="crash-recovery-dismiss-all"]');
    expect(button).toBeTruthy();
    button!.click();
    fixture.detectChanges();

    expect(dismissed).toHaveBeenCalledTimes(2);
    expect(dismissed).toHaveBeenNthCalledWith(1, 'a');
    expect(dismissed).toHaveBeenNthCalledWith(2, 'b');
    expect(fixture.componentInstance.pending()).toEqual([]);
    expect(fixture.componentInstance.busyAll()).toBe(false);
    expect(fixture.componentInstance.open()).toBe(false);
  });
});
