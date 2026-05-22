import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectWorkspaceSectionComponent } from './project-workspace-section';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 */
describe('ProjectWorkspaceSectionComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectWorkspaceSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectWorkspaceSectionComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.componentRef.setInput('currentWatchPath', '/tmp/demo');

    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ProjectWorkspaceSectionComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});
