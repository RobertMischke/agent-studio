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
      'Agent Studio': { executionHostId: 'local', integrationBranch: 'develop' },
    });

    fixture.componentInstance.assign('hetzner-agent-runner');
    const request = http.expectOne('/api/projects/Agent%20Studio/execution-host');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ hostId: 'hetzner-agent-runner' });
    request.flush({ executionHostId: 'hetzner-agent-runner' });

    expect(fixture.componentInstance.selectedHostId()).toBe('hetzner-agent-runner');
    http.verify();
  });
});
