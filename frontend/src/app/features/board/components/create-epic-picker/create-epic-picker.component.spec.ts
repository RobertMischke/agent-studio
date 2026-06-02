import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { CreateEpicPickerComponent } from './create-epic-picker.component';
import type { EpicRollup } from '../../../../models/task.model';

function epic(partial: Partial<EpicRollup> & Pick<EpicRollup, 'id' | 'watchPath'>): EpicRollup {
  return {
    title: partial.id,
    projectName: 'proj',
    state: '1-preparation',
    subTaskTotal: 0,
    completed: 0,
    inProgress: 0,
    open: 0,
    byState: {},
    subTasks: [],
    ...partial,
  };
}

describe('CreateEpicPickerComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(CreateEpicPickerComponent);
    const http = TestBed.inject(HttpTestingController);
    return { fixture, http };
  }

  it('lists only epics of the selected project', () => {
    const { fixture, http } = setup();
    fixture.componentRef.setInput('watchPath', '/projA');
    fixture.componentRef.setInput('show', true);
    fixture.detectChanges();

    http.expectOne((r) => r.url.endsWith('/epics')).flush([
      epic({ id: 'a1', watchPath: '/projA' }),
      epic({ id: 'b1', watchPath: '/projB' }),
    ]);
    fixture.detectChanges();

    const ids = fixture.componentInstance.epics().map((e) => e.id);
    expect(ids).toEqual(['a1']);
    expect(fixture.componentInstance.visible()).toBe(true);
  });

  it('stays hidden when kind is epic (show=false)', () => {
    const { fixture, http } = setup();
    fixture.componentRef.setInput('watchPath', '/projA');
    fixture.componentRef.setInput('show', false);
    fixture.detectChanges();

    http.expectOne((r) => r.url.endsWith('/epics')).flush([epic({ id: 'a1', watchPath: '/projA' })]);
    fixture.detectChanges();

    expect(fixture.componentInstance.visible()).toBe(false);
  });

  it('drops a stale selection when the project changes', () => {
    const { fixture, http } = setup();
    fixture.componentRef.setInput('watchPath', '/projA');
    fixture.componentRef.setInput('show', true);
    fixture.detectChanges();

    http.expectOne((r) => r.url.endsWith('/epics')).flush([
      epic({ id: 'a1', watchPath: '/projA' }),
      epic({ id: 'b1', watchPath: '/projB' }),
    ]);
    fixture.componentInstance.parentEpicId.set('a1');
    fixture.detectChanges();

    fixture.componentRef.setInput('watchPath', '/projB');
    fixture.detectChanges();

    expect(fixture.componentInstance.parentEpicId()).toBe('');
  });
});
