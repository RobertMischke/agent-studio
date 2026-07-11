import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ExecutionAssignmentCardComponent } from './execution-assignment-card';

describe('ExecutionAssignmentCardComponent', () => {
  it('loads and persists the project-dedicated host assignment', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { executionRunner: null, remoteExecutionEnabled: true, integrationBranch: 'develop' },
    });

    fixture.componentInstance.assign('agent-runner-01');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-runner');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      executionRunner: 'agent-runner-01',
      remoteExecutionEnabled: true,
    });
    request.flush({ executionRunner: 'agent-runner-01', remoteExecutionEnabled: true });

    expect(fixture.componentInstance.selectedHostId()).toBe('agent-runner-01');
    http.verify();
  });

  it('maps Local to the established null runner assignment', () => {
    TestBed.configureTestingModule({
      imports: [ExecutionAssignmentCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush({
      'Agent Studio': { executionRunner: 'agent-runner-01', remoteExecutionEnabled: true },
    });

    fixture.componentInstance.assign('local');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-runner');
    expect(request.request.body).toEqual({ executionRunner: null, remoteExecutionEnabled: true });
    request.flush({ executionRunner: null, remoteExecutionEnabled: true });

    expect(fixture.componentInstance.selectedHostId()).toBe('local');
    http.verify();
  });

  it('reports all four readiness checks independently', async () => {
    vi.useFakeTimers();
    try {
      TestBed.configureTestingModule({
        imports: [ExecutionAssignmentCardComponent],
        providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
      });
      const fixture = TestBed.createComponent(ExecutionAssignmentCardComponent);
      fixture.componentRef.setInput('projectName', 'Agent Studio');
      fixture.detectChanges();

      const http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/projects/settings').flush({
        'Agent Studio': { executionRunner: 'agent-runner-01', remoteExecutionEnabled: true, integrationBranch: 'develop' },
      });

      const probe = fixture.componentInstance.runProbe();
      await vi.runAllTimersAsync();
      await probe;

      expect(fixture.componentInstance.checks().map((check) => [check.key, check.state])).toEqual([
        ['code', 'passed'],
        ['branch', 'passed'],
        ['toolchain', 'passed'],
        ['noop', 'passed'],
      ]);
      expect(fixture.componentInstance.probePassed()).toBe(true);
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });
});
