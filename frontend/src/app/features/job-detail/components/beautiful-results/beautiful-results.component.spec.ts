import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { BeautifulResultsComponent } from './beautiful-results.component';

describe('BeautifulResultsComponent', () => {
  async function mount(markdown: string): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [BeautifulResultsComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([])
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(BeautifulResultsComponent);
    fixture.componentRef.setInput('markdown', markdown);
    fixture.componentRef.setInput('jobId', 'demo');
    fixture.componentRef.setInput('watchPath', 'C:/repo');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders a sentinel banner when markdown ends with a [[TASK_DONE]] token', async () => {
    const host = await mount('All good.\n\n[[TASK_DONE]]');
    const banner = host.querySelector('[data-testid="results-sentinel-banner"]');
    expect(banner).not.toBeNull();
    expect(banner?.getAttribute('data-kind')).toBe('done');
    // Body should not contain the raw sentinel any more.
    const body = host.querySelector('[data-testid="results-rendered"]');
    expect(body?.textContent ?? '').not.toMatch(/TASK_DONE/);
  });

  it('exposes both Rendered and Raw view-mode buttons', async () => {
    const host = await mount('# Hi');
    expect(host.querySelector('[data-testid="results-view-mode-rendered"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="results-view-mode-raw"]')).not.toBeNull();
  });

  it('renders an empty placeholder when no body content remains after sentinel extraction', async () => {
    const host = await mount('[[TASK_NOOP]]');
    expect(host.querySelector('[data-testid="results-empty"]')).not.toBeNull();
  });
});
