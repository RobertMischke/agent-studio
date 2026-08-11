import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TaskDetailLoadSectionsComponent } from './task-detail-load-sections.component';

describe('TaskDetailLoadSectionsComponent', () => {
  let fixture: ComponentFixture<TaskDetailLoadSectionsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskDetailLoadSectionsComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskDetailLoadSectionsComponent);
    fixture.componentRef.setInput('info', {
      id: 'task', key: 'AGT-2577', taskKey: 'watch::task', title: 'Heavy task',
      projectName: 'fixture', state: '5-human-review',
    });
    fixture.detectChanges();
  });

  it('renders an independently identified skeleton for every detail section', () => {
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-detail-head"]')?.textContent).toContain('AGT-2577');
    expect(root.querySelectorAll('[aria-busy="true"]')).toHaveLength(3);
    expect(root.querySelector('[data-testid="task-detail-section-context"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-activity"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-evidence"]')).not.toBeNull();
  });

  it('keeps each section visible with a retry action after failure', () => {
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);
    fixture.componentRef.setInput('errorMessage', 'The detail request timed out.');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(3);
    root.querySelector<HTMLButtonElement>('[data-testid="task-detail-section-retry-activity"]')?.click();
    expect(retried).toHaveBeenCalledOnce();
  });
});
