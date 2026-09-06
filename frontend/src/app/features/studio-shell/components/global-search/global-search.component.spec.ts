import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchComponent } from './global-search.component';
import type { GlobalSearchItem } from './global-search.service';

const DOSSIER: GlobalSearchItem = {
  domain: 'dossiers', projectName: 'Agent Studio', projectColor: '#569cd6',
  title: 'Watcher', subtitle: 'active · decision-ready', dossierKey: 'AGT-W15',
  dossierId: 'orchestrator-waechter', summary: 'Autonomous problem finding with ticket proposals.',
};

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

  it('renders a Dossier group with the key badge, status and phase chip, and the summary', () => {
    component.open.set(true);
    component.query.set('AGT-W15');
    component.remote.set({ commits: [], files: [], dossiers: [DOSSIER], errors: {} });
    fixture.detectChanges();

    const group: HTMLElement = fixture.nativeElement.querySelector('[data-testid="global-search-group-dossiers"]');
    expect(group).toBeTruthy();
    expect(group.textContent).toContain('AGT-W15');
    expect(group.textContent).toContain('Watcher');
    expect(group.textContent).toContain('active · decision-ready');
    expect(group.textContent).toContain('Autonomous problem finding with ticket proposals.');
  });

  it('opens the Dossier viewer for the selected dossier and keeps the four groups in one keyboard list', () => {
    const tabs = TestBed.inject(StudioTabStateService);
    const open = vi.spyOn(tabs, 'open').mockReturnValue(undefined);
    component.remote.set({ commits: [], files: [], dossiers: [DOSSIER], errors: {} });

    component.choose(DOSSIER);

    expect(open).toHaveBeenCalledWith({
      kind: 'workbench', projectName: 'Agent Studio', workbenchId: 'orchestrator-waechter',
      title: 'Watcher', key: 'AGT-W15',
    });
    expect(component.groups().map(group => group.domain))
      .toEqual(['tasks', 'dossiers', 'commits', 'files']);
  });
});
