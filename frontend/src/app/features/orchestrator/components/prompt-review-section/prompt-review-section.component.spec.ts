import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { PromptDetail } from '../../../../services/prompt-admin.service';
import { PromptReviewSectionComponent } from './prompt-review-section.component';

describe('PromptReviewSectionComponent', () => {
  it('renders review findings and project override provenance', async () => {
    await TestBed.configureTestingModule({
      imports: [PromptReviewSectionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PromptReviewSectionComponent);
    fixture.componentRef.setInput('detail', {
      review: {
        lastReviewedAt: '2026-07-23T10:00:00Z',
        reviewedBy: 'Robert',
        status: 'stale',
        findings: [{
          code: 'dead-prompt',
          severity: 'error',
          message: 'Prompt is unreachable.',
          projectName: null,
          stepId: null,
        }],
      },
      projectOverrides: [{
        projectName: 'Alpha',
        stepId: 'aspect-code-quality',
        promptName: 'review-aspect-code-quality.md',
        content: 'Project prompt',
        orphaned: false,
        matchesDefault: false,
        addedLines: 1,
        removedLines: 2,
      }],
    } as PromptDetail);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Prompt is unreachable');
    expect(host.textContent).toContain('Alpha');
    expect(host.textContent).toContain('aspect-code-quality');
  });
});
