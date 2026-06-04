import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { EpicOverviewScreenComponent, type EpicOverviewScope } from './epic-overview-screen.component';
import type { EpicRollup } from '../../../../models/task.model';

function rollup(over: Partial<EpicRollup>): EpicRollup {
  return {
    id: 'E1', title: 'Epic one', projectName: 'Acme', watchPath: '/repo/acme',
    state: '0-backlog', subTaskTotal: 0, completed: 0, inProgress: 0, open: 0,
    byState: {}, subTasks: [], ...over,
  };
}

function mount(scope: EpicOverviewScope | null, epics: EpicRollup[]) {
  TestBed.configureTestingModule({
    imports: [EpicOverviewScreenComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(EpicOverviewScreenComponent);
  fixture.componentRef.setInput('scopedProject', scope);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne((r) => r.url.endsWith('/epics')).flush(epics);
  fixture.detectChanges();
  return { fixture, http };
}

function testid(fixture: { nativeElement: HTMLElement }, id: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
}

describe('EpicOverviewScreenComponent', () => {
  it('invites creation when a scoped project has no epics', () => {
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    expect(testid(host, 'epic-overview-empty')).toBeTruthy();
    expect(testid(host, 'epic-overview-create')).toBeTruthy();
    expect(testid(host, 'epic-overview-new')).toBeTruthy();
    http.verify();
  });

  it('does not offer creation in the unscoped cross-project view', () => {
    const { fixture, http } = mount(null, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    expect(testid(host, 'epic-overview-empty')).toBeTruthy();
    expect(testid(host, 'epic-overview-create')).toBeNull();
    expect(testid(host, 'epic-overview-new')).toBeNull();
    http.verify();
  });

  it('shows only the scoped project\'s epics', () => {
    const epics = [
      rollup({ id: 'E1', projectName: 'Acme' }),
      rollup({ id: 'E2', projectName: 'Other' }),
    ];
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, epics);
    expect(fixture.componentInstance.visibleEpics().map((e) => e.id)).toEqual(['E1']);
    http.verify();
  });

  it('opens the create dialog from the invite button', () => {
    const { fixture, http } = mount({ name: 'Acme', watchPath: '/repo/acme' }, []);
    const host = { nativeElement: fixture.nativeElement as HTMLElement };
    (testid(host, 'epic-overview-create') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.showCreate()).toBe(true);
    expect(testid(host, 'epic-create-dialog')).toBeTruthy();
    http.verify();
  });
});
