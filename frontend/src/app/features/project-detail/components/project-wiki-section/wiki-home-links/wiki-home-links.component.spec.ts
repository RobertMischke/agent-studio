import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { WikiHomeLinksComponent, WikiHomeOpenRequest } from './wiki-home-links.component';
import type { WikiHome } from '../../../../../models/project-docs.model';

const HOME: WikiHome = {
  sections: [
    {
      title: 'Start',
      links: [
        { relPath: 'concepts/overview.md', label: 'Konzept-Überblick', note: 'Der Einstieg', exists: true },
        { relPath: 'workbench/overview.html', label: 'Dossier', note: null, exists: true },
        { relPath: 'missing/gone.md', label: 'Verschollen', note: 'alte Seite', exists: false },
      ],
    },
    {
      title: 'Betrieb',
      links: [{ relPath: 'ops/runbook.md', label: 'Runbook', note: null, exists: true }],
    },
    { title: 'Leer', links: [] },
  ],
};

async function setup(home: WikiHome | 'error' = HOME) {
  await TestBed.configureTestingModule({
    imports: [WikiHomeLinksComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(WikiHomeLinksComponent);
  const http = TestBed.inject(HttpTestingController);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();

  const request = http.expectOne('/api/projects/Demo/wiki/home');
  if (home === 'error') {
    request.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
  } else {
    request.flush(home);
  }
  fixture.detectChanges();
  return { fixture, http };
}

const el = (f: { nativeElement: unknown }) => f.nativeElement as HTMLElement;

describe('WikiHomeLinksComponent', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('renders the curated sections with labels and dimmed notes; empty sections are dropped', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);

    expect(root.querySelector('[data-testid="wiki-home-links"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-home-section-Start"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-home-section-Betrieb"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="wiki-home-section-Leer"]')).toBeNull();

    const startLink = root.querySelector('[data-testid="wiki-home-link-concepts/overview.md"]')!;
    expect(startLink.textContent).toContain('Konzept-Überblick');
    expect(startLink.querySelector('.whome__note')!.textContent).toContain('Der Einstieg');
    http.verify();
  });

  it('emits navigation for existing links, never for exists=false (dimmed) links', async () => {
    const { fixture, http } = await setup();
    const root = el(fixture);
    const opened: WikiHomeOpenRequest[] = [];
    fixture.componentInstance.openLink.subscribe(request => opened.push(request));

    // exists=false renders dimmed (no button) and does not navigate on click.
    const missing = root.querySelector<HTMLElement>('[data-testid="wiki-home-link-missing/gone.md"]')!;
    expect(missing.classList.contains('whome__link--missing')).toBe(true);
    expect(missing.tagName.toLowerCase()).not.toBe('button');
    missing.click();
    expect(opened).toEqual([]);

    root.querySelector<HTMLButtonElement>('[data-testid="wiki-home-link-concepts/overview.md"]')!.click();
    root.querySelector<HTMLButtonElement>('[data-testid="wiki-home-link-workbench/overview.html"]')!.click();
    expect(opened).toEqual([
      { relPath: 'concepts/overview.md', type: 'md' },
      { relPath: 'workbench/overview.html', type: 'html' },
    ]);
    http.verify();
  });

  it('renders nothing when the home payload cannot be loaded', async () => {
    const { fixture, http } = await setup('error');
    expect(el(fixture).querySelector('[data-testid="wiki-home-links"]')).toBeNull();
    http.verify();
  });
});
