import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, describe, expect, it } from 'vitest';
import { taskCardNow } from '../task-card/task-card-clock';
import { TaskCardQuotaWaitComponent } from './task-card-quota-wait.component';

describe('TaskCardQuotaWaitComponent', () => {
  afterEach(() => taskCardNow.set(Date.now()));

  it('renders a visible reset time and countdown from the durable wait state', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardQuotaWaitComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardQuotaWaitComponent);
    fixture.componentRef.setInput('wait', {
      cliType: 'codex',
      startedAt: '2026-07-22T11:02:00.000Z',
      resetAt: '2026-07-22T11:14:00.000Z',
      thresholdMinutes: 30,
      reason: 'Confirmed nearby quota reset',
    });
    taskCardNow.set(Date.parse('2026-07-22T11:02:30.000Z'));
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-quota-wait"]') as HTMLElement;
    expect(pill).toBeTruthy();
    expect(pill.textContent).toContain('12 min remaining');
    expect(pill.getAttribute('data-minutes-left')).toBe('12');
  });
});
