import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { DecisionBacklogHintComponent } from './decision-backlog-hint.component';

function task(key: string, count: number, title = key): TaskInfo {
  return {
    id: key.toLowerCase(),
    key,
    displayKey: key,
    taskKey: `/workspace::${key}`,
    title,
    state: '5-human-review',
    order: 1,
    agent: 'claude',
    createdAt: '',
    watchPath: '/workspace',
    projectName: 'Agent Studio',
    folderPath: '',
    lastActivity: '',
    transitiveWaiters: {
      count,
      keys: Array.from({ length: count }, (_, index) => `WAIT-${index + 1}`),
    },
  } as TaskInfo;
}

describe('DecisionBacklogHintComponent', () => {
  it('sorts human decisions by descending transitive impact', async () => {
    await TestBed.configureTestingModule({
      imports: [DecisionBacklogHintComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DecisionBacklogHintComponent);
    fixture.componentRef.setInput('tasks', [task('AGT-2', 2), task('AGT-9', 9), task('AGT-4', 4)]);
    fixture.detectChanges();

    expect(fixture.componentInstance.entries().map((entry) => entry.key))
      .toEqual(['AGT-9', 'AGT-4', 'AGT-2']);
    const host = fixture.nativeElement as HTMLElement;
    expect(Array.from(host.querySelectorAll<HTMLElement>('.decision-backlog__key'))
      .map((element) => element.textContent?.trim()))
      .toEqual(['AGT-9', 'AGT-4', 'AGT-2']);
  });

  it.each([
    {
      count: 1,
      expectedSummary: 'Your decision on AGT-9 blocks 1 waiting card',
      details: 'This card is waiting for your decision:',
    },
    {
      count: 3,
      expectedSummary: 'Your decision on AGT-9 blocks 3 waiting cards',
      details: 'These cards are waiting for your decision:',
    },
  ])('pluralizes the English impact copy for $count waiting card(s)', async ({ count, expectedSummary, details }) => {
    await TestBed.configureTestingModule({
      imports: [DecisionBacklogHintComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DecisionBacklogHintComponent);
    const decision = task('AGT-9', count);
    const waitingTasks = Array.from({ length: count }, (_, index) =>
      task(`WAIT-${index + 1}`, 0, `Waiting implementation ${index + 1}`));
    fixture.componentRef.setInput('tasks', [decision]);
    fixture.componentRef.setInput('allTasks', [decision, ...waitingTasks]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const summaryButton = host.querySelector<HTMLButtonElement>('[data-testid="decision-backlog-item-AGT-9"]')!;
    expect(summaryButton.textContent?.replace(/\s+/g, ' ').trim()).toContain(expectedSummary);
    expect(host.querySelector('.decision-backlog__waiters p')?.textContent?.replace(/\s+/g, ' ').trim())
      .toBe(details);
  });

  it('opens a resolved waiting card and preserves its user-authored title', async () => {
    await TestBed.configureTestingModule({
      imports: [DecisionBacklogHintComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DecisionBacklogHintComponent);
    const decision = task('AGT-9', 1);
    const waiting = task('WAIT-1', 0, 'Runner-Freigabe umsetzen');
    const opened: TaskInfo[] = [];
    fixture.componentInstance.taskClick.subscribe((value) => opened.push(value));
    fixture.componentRef.setInput('tasks', [decision]);
    fixture.componentRef.setInput('allTasks', [decision, waiting]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const summary = host.querySelector<HTMLButtonElement>('[data-testid="decision-backlog-item-AGT-9"]')!;
    summary.click();
    fixture.detectChanges();
    expect(summary.getAttribute('aria-expanded')).toBe('true');
    const waiter = host.querySelector<HTMLButtonElement>('[data-testid="decision-backlog-waiter-WAIT-1"]')!;
    expect(waiter.textContent).toContain('WAIT-1');
    expect(waiter.textContent).toContain('Runner-Freigabe umsetzen');
    waiter.click();
    expect(opened).toEqual([waiting]);
  });

  it('renders no hint when no decision dams another card', async () => {
    await TestBed.configureTestingModule({
      imports: [DecisionBacklogHintComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DecisionBacklogHintComponent);
    fixture.componentRef.setInput('tasks', []);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="decision-backlog"]')).toBeNull();
  });
});
