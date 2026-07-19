import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DiffContentComponent } from './diff-content.component';
import { loadDiff2Html } from '../../utils/diff2html-lazy';

/**
 * Focused unit coverage for the shared diff renderer. The full diff2html path
 * is exercised end-to-end by the surfaces that consume this component (Studio
 * diff tab, Project Hub Git View); here we pin the two deterministic contracts:
 *  - an empty diff renders nothing (no stray placeholder);
 *  - a non-empty diff renders diff2html HTML once the module is loaded.
 * The module is pre-warmed so its dynamic import resolves inside the test
 * rather than after the environment is torn down.
 */
describe('DiffContentComponent', () => {
  function mount(diffText: string) {
    TestBed.configureTestingModule({
      imports: [DiffContentComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(DiffContentComponent);
    fixture.componentRef.setInput('diffText', diffText);
    fixture.detectChanges();
    return fixture;
  }

  it('renders nothing for an empty diff', () => {
    const fixture = mount('');
    const root = fixture.nativeElement as HTMLElement;
    expect(fixture.componentInstance.html()).toBeNull();
    expect(root.querySelector('[data-testid="diff-content-render"]')).toBeNull();
    expect(root.querySelector('[data-testid="diff-content-preparing"]')).toBeNull();
  });

  it('renders diff2html output for a non-empty diff', async () => {
    // Warm the shared lazy module so the component renders synchronously and
    // no dynamic import resolves after teardown.
    await loadDiff2Html();
    const fixture = mount('diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -0,0 +1 @@\n+added\n');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="diff-content-render"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="diff-content-preparing"]')).toBeNull();
  });
});
