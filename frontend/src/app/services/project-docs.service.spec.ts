import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectDocsService, sanitizeWikiSearchSnippet } from './project-docs.service';
import type { WikiSearchResponse } from '../models/project-docs.model';

describe('sanitizeWikiSearchSnippet', () => {
  it('keeps literal <em> highlight tags', () => {
    expect(sanitizeWikiSearchSnippet('Der <em>Guide</em> für alles'))
      .toBe('Der <em>Guide</em> für alles');
  });

  it('escapes every other tag, including attribute-carrying em variants', () => {
    expect(sanitizeWikiSearchSnippet('<img src=x onerror=alert(1)>'))
      .toBe('&lt;img src=x onerror=alert(1)>');
    expect(sanitizeWikiSearchSnippet('<em onclick=alert(1)>x</em>'))
      .toBe('&lt;em onclick=alert(1)>x</em>');
    expect(sanitizeWikiSearchSnippet('<script>alert(1)</script>'))
      .toBe('&lt;script>alert(1)&lt;/script>');
  });

  it('does not double-escape already-escaped content', () => {
    expect(sanitizeWikiSearchSnippet('a &lt;b&gt; <em>c</em> &amp; d'))
      .toBe('a &lt;b&gt; <em>c</em> &amp; d');
  });
});

describe('ProjectDocsService.searchWiki', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
  });

  it('sends q (plus semantic/limit only when set) and sanitises snippets', () => {
    const service = TestBed.inject(ProjectDocsService);
    const http = TestBed.inject(HttpTestingController);

    let received: WikiSearchResponse | undefined;
    service.searchWiki('Demo', 'guide').subscribe(response => (received = response));
    const plain = http.expectOne(r => r.url === '/api/projects/Demo/wiki/search');
    expect(plain.request.params.get('q')).toBe('guide');
    expect(plain.request.params.has('semantic')).toBe(false);
    expect(plain.request.params.has('limit')).toBe(false);
    plain.flush({
      query: 'guide', semanticUsed: false, expandedTerms: [], durationMs: 3,
      results: [{
        relPath: 'a.md', title: 'A', kind: 'md', score: 1, updatedAt: null,
        snippet: '<em>hit</em> <img src=x>',
      }],
    });
    expect(received?.results[0].snippet).toBe('<em>hit</em> &lt;img src=x>');

    service.searchWiki('Demo', 'guide', { semantic: true, limit: 5 }).subscribe();
    const semantic = http.expectOne(r => r.url === '/api/projects/Demo/wiki/search');
    expect(semantic.request.params.get('semantic')).toBe('true');
    expect(semantic.request.params.get('limit')).toBe('5');
    semantic.flush({ query: 'guide', semanticUsed: true, expandedTerms: [], durationMs: 3, results: [] });
    http.verify();
  });
});
