import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { WikiAgentReadsComponent } from './wiki-agent-reads.component';

describe('WikiAgentReadsComponent', () => {
  it('renders the total, last-read timestamp, and recent task keys', async () => {
    await TestBed.configureTestingModule({
      imports: [WikiAgentReadsComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WikiAgentReadsComponent);
    fixture.componentRef.setInput('reads', {
      total: 2,
      lastReadAt: '2026-07-22T10:15:00Z',
      recent: [
        { at: '2026-07-22T10:15:00Z', taskKey: 'AGT-2242' },
        { at: '2026-07-21T09:00:00Z', taskKey: 'AGT-2200' },
      ],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="project-wiki-agent-reads-total"]')?.textContent?.trim()).toBe('2');
    expect(root.querySelector('[data-testid="project-wiki-agent-reads-last"]')?.textContent).toContain('2026');
    expect(root.querySelector('[data-testid="project-wiki-agent-reads-recent"]')?.textContent).toContain('AGT-2242');
  });
});
