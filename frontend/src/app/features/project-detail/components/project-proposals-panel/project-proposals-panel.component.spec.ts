import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ProjectProposalsPanelComponent } from './project-proposals-panel.component';

describe('ProjectProposalsPanelComponent', () => {
  it('renders a proposal and spawns an implementation card on approval', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectProposalsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectProposalsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Agent Task Processor');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const item = { id: 'survey-001', generation: '2026-07-11', finding: 'Navigation is clipped.', evidenceScreenshot: '2026-07-11/assets/001.png', proposal: 'Make navigation responsive.', estimatedEffort: 'medium', severity: 'critical', status: 'proposed', spawnedTask: null, topic: 'Responsiveness', categories: ['responsiveness', 'navigation'], source: 'Visual survey: narrow-board.png', rejectionReason: null, rejectionReasonRaw: null, relPath: '2026-07-11/survey-001.md', updatedAt: new Date().toISOString() };
    http.expectOne('/api/projects/Agent%20Task%20Processor/proposals').flush({ items: [item] });
    await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Make navigation responsive.');
    expect(fixture.nativeElement.querySelector('[data-testid="proposal-image-loading"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Review decision');
    expect(fixture.nativeElement.textContent).toContain('Generation');
    expect(fixture.nativeElement.querySelector('[data-testid="proposal-topic"]').textContent).toContain('Responsiveness');
    fixture.nativeElement.querySelector('.proposal-detail__hero img').dispatchEvent(new Event('load'));
    await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="proposal-image-loading"]')).toBeNull();
    fixture.nativeElement.querySelector('[data-testid="proposal-approve"]').click();
    http.expectOne('/api/projects/Agent%20Task%20Processor/proposals/survey-001/decision').flush({ proposal: { ...item, status: 'spawned', spawnedTask: 'AGT-3000' } });
    http.expectOne('/api/tasks/reference-status').flush({ items: [] });
  });
});
