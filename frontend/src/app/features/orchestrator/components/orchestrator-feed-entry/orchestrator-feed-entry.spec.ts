import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { OrchestratorFeedEntryComponent } from './orchestrator-feed-entry';

describe('OrchestratorFeedEntryComponent', () => {
  it('renders project identity and emits project filtering without selecting the row', async () => {
    await TestBed.configureTestingModule({
      imports: [OrchestratorFeedEntryComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorFeedEntryComponent);
    fixture.componentRef.setInput('entry', {
      ts: '2026-08-10T08:00:00Z', kind: 'decision', topic: 'watcher/decision',
      summary: 'Decision required', project: 'Agent Studio', jobId: null, tokenUsage: null,
    });
    const projects: string[] = [];
    const selections: unknown[] = [];
    fixture.componentInstance.projectFilterRequest.subscribe(project => projects.push(project));
    fixture.componentInstance.selectRequest.subscribe(entry => selections.push(entry));
    fixture.detectChanges();

    const chip = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="orchestrator-entry-project"]')!;
    expect(chip.textContent?.trim()).toBe('AS');
    chip.click();
    expect(projects).toEqual(['Agent Studio']);
    expect(selections).toEqual([]);
  });
});
