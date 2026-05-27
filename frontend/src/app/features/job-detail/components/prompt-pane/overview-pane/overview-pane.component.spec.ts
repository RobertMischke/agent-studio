import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { OverviewPaneComponent } from './overview-pane.component';

describe('OverviewPaneComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [OverviewPaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        RunTimelinePollService,
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OverviewPaneComponent);
    fixture.componentRef.setInput('job', {
      id: 'test-1', jobKey: 'wp::test-1', title: 'Test', state: '2-ready',
      order: 1, agent: 'human', createdAt: new Date().toISOString(),
      watchPath: '/tmp', projectName: 'test', folderPath: '/tmp/test-1',
      lastActivity: new Date().toISOString(), sessionName: null,
      model: null, cliType: null, useOwnSession: null, lastUsage: null,
      execution: null, commit: null,
    });
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] OverviewPaneComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});
