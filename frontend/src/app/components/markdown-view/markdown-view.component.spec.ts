import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { MarkdownViewComponent } from './markdown-view.component';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';

describe('MarkdownViewComponent', () => {
  function setup(taskRefs?: Partial<TaskReferenceNavigationService>) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        ...(taskRefs ? [{ provide: TaskReferenceNavigationService, useValue: taskRefs }] : []),
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

  it('opens task references through the task-tab navigation service', () => {
    let opened: string | null = null;
    const fixture = setup({
      markdownReferences: signal([{ label: 'ASS-738', taskKey: 'project::ass-738' }]).asReadonly(),
      openTaskKey: (taskKey: string | null | undefined) => {
        opened = taskKey ?? null;
        return true;
      },
    });
    fixture.componentRef.setInput('source', 'See ASS-738.');
    fixture.detectChanges();

    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body')!;
    const anchor = body.querySelector<HTMLAnchorElement>('a[data-task-ref="true"]')!;
    expect(anchor).toBeTruthy();
    anchor.click();

    expect(opened).toBe('project::ass-738');
  });

  it('links task references in pre-rendered html without trusting unsafe html', () => {
    const fixture = setup({
      markdownReferences: signal([{ label: 'ASS-738', taskKey: 'project::ass-738' }]).asReadonly(),
      openTaskKey: () => true,
    });
    fixture.componentRef.setInput('html', '<img src=x onerror="alert(1)"><p>See ASS-738.</p><script>alert(2)</script>');
    fixture.detectChanges();

    const body = (fixture.nativeElement as HTMLElement).querySelector('.markdown-body')!;
    expect(body.querySelector('script')).toBeNull();
    expect(body.querySelector('img')?.getAttribute('onerror')).toBeNull();
    expect(body.querySelector('a[data-task-ref="true"]')?.textContent).toBe('ASS-738');
  });
});
