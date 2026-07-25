import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { StudioWelcomeComponent } from './studio-welcome.component';

describe('StudioWelcomeComponent', () => {
  it('promotes project chat without advertising direct task creation', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioWelcomeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    const fixture = TestBed.createComponent(StudioWelcomeComponent);
    fixture.componentRef.setInput('projects', [{
      name: 'Agent Software Studio',
      initial: 'A',
      color: 'currentColor',
      totalJobs: 3,
      laneCounts: { ready: 1, progress: 1, humanReview: 1 },
      isActive: false,
    }]);
    const chatOpened = vi.fn();
    fixture.componentInstance.chatOpened.subscribe(chatOpened);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Describe your first task in the project chat.');
    expect(fixture.nativeElement.textContent).not.toContain('New task');
    fixture.nativeElement.querySelector('[data-testid="studio-welcome-open-chat"]')?.click();
    expect(chatOpened).toHaveBeenCalledOnce();
  });
});
