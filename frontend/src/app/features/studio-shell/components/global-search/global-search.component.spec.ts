import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { TaskInfo } from '../../../../models/task.model';
import { GlobalSearchComponent } from './global-search.component';

describe('GlobalSearchComponent', () => {
  let fixture: ComponentFixture<GlobalSearchComponent>;
  let component: GlobalSearchComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GlobalSearchComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(GlobalSearchComponent);
    component = fixture.componentInstance;
  });

  it('opens with Ctrl+K and closes with Escape', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    expect(component.open()).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.open()).toBe(false);
  });

  it('ranks an exact task key before a title match from in-memory board state', () => {
    fixture.componentRef.setInput('tasks', [
      { taskKey: 'a', key: 'AGT-20', title: 'AGT-2034 follow-up', projectName: 'P', state: '2-ready', id: 'a' },
      { taskKey: 'b', key: 'AGT-2034', title: 'Global search', projectName: 'P', state: '3-progress', id: 'b' },
    ] as TaskInfo[]);
    component.query.set('AGT-2034');

    expect(component.taskResults().map(x => x.taskKey)).toEqual(['b', 'a']);
  });
});
