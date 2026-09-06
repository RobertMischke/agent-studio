import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ReleaseTaskButtonComponent } from './release-task-button.component';
import { TaskService } from '../../services/task.service';
import { NotificationService } from '../../services/notification.service';

describe('ReleaseTaskButtonComponent', () => {
  it('calls the release service and emits the new flag', async () => {
    const setTaskReleased = vi.fn().mockReturnValue(of({ released: true }));
    const refresh = vi.fn();
    await TestBed.configureTestingModule({
      imports: [ReleaseTaskButtonComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { setTaskReleased, refresh } },
        { provide: NotificationService, useValue: { success: vi.fn(), error: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(ReleaseTaskButtonComponent);
    fixture.componentRef.setInput('targetId', 'target-1');
    fixture.componentRef.setInput('targetKey', 'LIB-1');
    fixture.componentRef.setInput('watchPath', '/workspace/lib');
    const emitted = vi.fn();
    fixture.componentInstance.releaseChanged.subscribe(emitted);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    expect(setTaskReleased).toHaveBeenCalledWith('target-1', true, '/workspace/lib');
    expect(emitted).toHaveBeenCalledWith(true);
    expect(refresh).toHaveBeenCalledWith(true);
  });
});
