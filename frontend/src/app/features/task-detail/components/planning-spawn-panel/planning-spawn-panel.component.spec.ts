import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import { PlanningSpawnPanelComponent } from './planning-spawn-panel.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import type { PlanningSpawnSummary, TaskInfo, TaskMode } from '../../../../models/task.model';

function summary(partial: Partial<PlanningSpawnSummary>): PlanningSpawnSummary {
  return {
    spawned: [],
    spawnedCount: 0,
    noFollowUpDeclared: false,
    contractSatisfied: false,
    ...partial,
  };
}

function job(mode: TaskMode, spawn: PlanningSpawnSummary | null): TaskInfo {
  return {
    id: 'job-1',
    taskKey: 'wp::job-1',
    title: 'Plan the thing',
    state: '5-human-review',
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-10T00:00:00Z',
    watchPath: '/wp',
    projectName: 'WP',
    folderPath: '/wp/job-1',
    lastActivity: '2026-07-10T00:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    mode,
    planningSpawn: spawn,
  };
}

async function mount(info: TaskInfo, taskService: Partial<TaskService> = {}) {
  await TestBed.configureTestingModule({
    imports: [PlanningSpawnPanelComponent],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskService, useValue: { setPlanningClosure: vi.fn(), ...taskService } },
      { provide: NotificationService, useValue: { success: vi.fn(), warning: vi.fn() } },
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(PlanningSpawnPanelComponent);
  fixture.componentRef.setInput('job', info);
  fixture.detectChanges();
  return fixture;
}

describe('PlanningSpawnPanelComponent (AGT-2069)', () => {
  it('is not rendered for a coding task', async () => {
    const fixture = await mount(job('coding', null));
    expect(fixture.nativeElement.querySelector('[data-testid="planning-spawn-panel"]')).toBeNull();
  });

  it('shows the "no follow-up cards" warning + risk contract when nothing spawned or declared', async () => {
    const fixture = await mount(job('planning', summary({ contractSatisfied: false })));
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="planning-spawn-panel"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="planning-no-followups-warning"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="planning-contract"]')!.textContent).toContain('no follow-ups');
    expect(host.querySelector('[data-testid="planning-declare-open"]')).toBeTruthy();
  });

  it('renders spawned follow-ups (as key chips before hydration) and a met contract', async () => {
    const fixture = await mount(
      job('planning', summary({
        spawned: [{ targetKey: 'WEB-42', at: '2026-07-10T00:00:00Z' }],
        spawnedCount: 1,
        contractSatisfied: true,
      })),
    );
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="planning-spawn-cards"]')!.textContent).toContain('WEB-42');
    expect(host.querySelector('[data-testid="planning-contract"]')!.textContent).toContain('contract met');
    // The panel hydrates the spawned keys through the reference-status endpoint.
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/tasks/reference-status').flush({ items: [] });
    http.verify();
  });

  it('declaring "no follow-up intended" calls the closure endpoint and shows the declared state', async () => {
    const declared = summary({ noFollowUpDeclared: true, noFollowUpReason: 'archived', contractSatisfied: true });
    const setPlanningClosure = vi.fn(() => of(declared));
    const fixture = await mount(job('planning', summary({ contractSatisfied: false })), { setPlanningClosure });
    const host = fixture.nativeElement as HTMLElement;

    (host.querySelector('[data-testid="planning-declare-open"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.componentInstance.reasonDraft.set('archived');
    (host.querySelector('[data-testid="planning-declare-submit"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(setPlanningClosure).toHaveBeenCalledWith('job-1', true, 'archived', '/wp');
    expect(host.querySelector('[data-testid="planning-no-followup-declared"]')).toBeTruthy();
    expect(host.querySelector('[data-testid="planning-contract"]')!.textContent).toContain('contract met');
  });
});
