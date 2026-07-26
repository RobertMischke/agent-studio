import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { DecisionBacklogHintComponent } from './decision-backlog-hint.component';

function task(key: string, count: number): TaskInfo {
  return {
    id: key.toLowerCase(),
    key,
    displayKey: key,
    taskKey: `/workspace::${key}`,
    title: key,
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
