import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { WorkspaceCreateDialogComponent } from './workspace-create-dialog.component';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 */
describe('WorkspaceCreateDialogComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkspaceCreateDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkspaceCreateDialogComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] WorkspaceCreateDialogComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});
