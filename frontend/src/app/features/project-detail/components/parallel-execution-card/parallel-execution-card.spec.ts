import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ParallelExecutionCardComponent } from './parallel-execution-card';

/**
 * The card exists because the local ProjectRunner still limits itself by the
 * per-project maxParallelism (ParallelSlotPolicy). It must therefore be visible
 * and writable for local projects, and absent for remote ones, where the host
 * ceiling is the only source of truth (AGT-2302 / AGT-2376).
 */
describe('ParallelExecutionCardComponent', () => {
  function mount(settings: Record<string, { maxParallelism?: number; executionLocation?: string }>) {
    TestBed.configureTestingModule({
      imports: [ParallelExecutionCardComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ParallelExecutionCardComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/settings').flush(settings);
    fixture.detectChanges();
    return { fixture, http };
  }

  it('edits the still-live local setting through the restored route', () => {
    const { fixture, http } = mount({
      'Agent Studio': { maxParallelism: 2, executionLocation: 'local' },
    });
    const select: HTMLSelectElement = fixture.nativeElement
      .querySelector('[data-testid="project-settings-max-parallelism"]');
    expect(select).toBeTruthy();
    expect(select.value).toBe('2');

    select.value = '3';
    select.dispatchEvent(new Event('change'));
    const request = http.expectOne('/api/projects/Agent%20Studio/max-parallelism');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ maxParallelism: 3 });
    request.flush({});
    http.verify();
  });

  it('stays hidden for a project that executes on a remote host', () => {
    const { fixture, http } = mount({
      'Agent Studio': { maxParallelism: 2, executionLocation: 'agent-runner-01' },
    });
    expect(fixture.nativeElement.querySelector('[data-testid="project-settings-parallel"]'))
      .toBeNull();
    http.verify();
  });
});
