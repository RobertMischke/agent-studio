import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskInspectorTabComponent } from './task-inspector-tab.component';

describe('TaskInspectorTabComponent', () => {
  let fixture: ComponentFixture<TaskInspectorTabComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskInspectorTabComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskInspectorTabComponent);
  });

  it('renders the task markdown and quiet refinement metadata in chronological input order', async () => {
    fixture.componentRef.setInput('promptMarkdown', '# Original task\n\nBuild the tab.');
    fixture.componentRef.setInput('refinements', [
      {
        id: 'operator-1',
        at: '2026-07-28T09:05:00Z',
        actor: 'operator',
        reason: 'steer follow-up',
        markdown: 'Keep the layout calm.',
        source: 'run-log',
        runIndex: 2,
      },
      {
        id: 'system-1',
        at: '2026-07-28T09:10:00Z',
        actor: 'system',
        reason: 'Missing regression coverage',
        markdown: 'Add a browser test.',
        source: 'orchestrator-history',
        runIndex: null,
      },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="task-tab-prompt"]')?.textContent).toContain('Original task');
    const entries = [...element.querySelectorAll('[data-testid="task-refinement-entry"]')];
    expect(entries).toHaveLength(2);
    expect(entries[0].textContent).toContain('Operator');
    expect(entries[0].textContent).toContain('steer follow-up');
    expect(entries[1].textContent).toContain('System');
    expect(entries[1].textContent).toContain('Missing regression coverage');
  });
});
