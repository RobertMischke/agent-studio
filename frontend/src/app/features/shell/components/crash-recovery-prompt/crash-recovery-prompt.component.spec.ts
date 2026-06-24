import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
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
});
