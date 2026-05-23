import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { MarkdownViewComponent } from './markdown-view.component';

describe('MarkdownViewComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    return TestBed.createComponent(MarkdownViewComponent);
  }

  it('renders markdown source into the body via [source]', () => {
    const fixture = setup();
    fixture.componentRef.setInput('source', '# Hello\n\n- one\n- two');
    fixture.detectChanges();
    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body');
    expect(body).toBeTruthy();
    expect(body!.innerHTML).toContain('<h1>Hello</h1>');
    expect(body!.innerHTML).toContain('<ul>');
  });

  it('prefers pre-rendered [html] over [source] (F22 path)', () => {
    const fixture = setup();
    fixture.componentRef.setInput('source', '# Source wins');
    fixture.componentRef.setInput('html', '<h2>Server rendered</h2>');
    fixture.detectChanges();
    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body');
    expect(body!.innerHTML).toContain('Server rendered');
    expect(body!.innerHTML).not.toContain('Source wins');
  });

  it('applies the dense modifier when [dense] is true', () => {
    const fixture = setup();
    fixture.componentRef.setInput('source', 'body text');
    fixture.componentRef.setInput('dense', true);
    fixture.detectChanges();
    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body');
    expect(body!.classList.contains('markdown-body--dense')).toBe(true);
  });

  it('opts into numbered code blocks when codeLineNumbers is true', () => {
    const fixture = setup();
    fixture.componentRef.setInput(
      'source',
      '```\nl1\nl2\nl3\nl4\nl5\nl6\n```',
    );
    fixture.componentRef.setInput('codeLineNumbers', true);
    fixture.detectChanges();
    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body');
    expect(body!.innerHTML).toContain('md-code--numbered');
  });
});
