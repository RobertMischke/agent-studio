import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiSourceBadgeComponent } from './wiki-source-badge.component';

describe('WikiSourceBadgeComponent', () => {
  it('renders branch and short commit', async () => {
    await TestBed.configureTestingModule({
      imports: [WikiSourceBadgeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WikiSourceBadgeComponent);
    fixture.componentRef.setInput('source', {
      mode: 'branch', branch: 'origin/develop', commit: 'abcdef1234',
      shortCommit: 'abcdef12', writable: false, error: null,
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('origin/develop @ abcdef12');
  });
});
