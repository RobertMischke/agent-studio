import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectShellComponent } from './project-shell.component';
import type { ProjectRailKey } from './project-shell.config';

/**
 * Render-path coverage for the Project-Hub nav IA (ASS-1711): collapsible
 * main segments, tree-expandable parents (Steering Docs / Settings), the
 * "Agent Docs" rename, the standalone "Runtime Prompts" point, and the
 * text-only rail (no per-item icons).
 */
function mount(activeRail: ProjectRailKey = 'overview') {
  TestBed.configureTestingModule({
    imports: [ProjectShellComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  const fixture = TestBed.createComponent(ProjectShellComponent);
  fixture.componentRef.setInput('projectName', 'Demo Project');
  fixture.componentRef.setInput('activeRail', activeRail);
  fixture.detectChanges();
  return fixture;
}

function railEl(host: HTMLElement, key: string): HTMLElement | null {
  return host.querySelector<HTMLElement>(`[data-testid="project-shell-rail-${key}"]`);
}

describe('ProjectShellComponent rail IA', () => {
  it('renders all four collapsible main segments', () => {
    const host = mount().nativeElement as HTMLElement;
    for (const id of ['insight', 'quality', 'operations', 'config']) {
      const header = host.querySelector(`[data-testid="project-shell-group-${id}"]`);
      expect(header, `segment header ${id}`).toBeTruthy();
      expect(header!.getAttribute('aria-expanded')).toBe('true');
    }
  });

  it('collapsing a segment hides its items, expanding shows them again', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    const insightHeader = host.querySelector<HTMLElement>('[data-testid="project-shell-group-insight"]')!;

    expect(railEl(host, 'overview')).toBeTruthy();
    insightHeader.click();
    fixture.detectChanges();
    expect(insightHeader.getAttribute('aria-expanded')).toBe('false');
    expect(railEl(host, 'overview')).toBeNull();

    insightHeader.click();
    fixture.detectChanges();
    expect(railEl(host, 'overview')).toBeTruthy();
  });

  it('groups documentation rails under a non-navigable "Steering Docs" tree parent', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;

    // The container row is present and labelled, and ships a disclosure twisty.
    const container = railEl(host, 'steering-docs')!;
    expect(container.textContent).toContain('Steering Docs');
    expect(host.querySelector('[data-testid="project-shell-twisty-steering-docs"]')).toBeTruthy();

    // Children are visible by default (parents seed expanded).
    expect(railEl(host, 'architecture')).toBeTruthy();
    expect(railEl(host, 'wiki')).toBeTruthy();
    expect(railEl(host, 'steering')).toBeTruthy();

    // Collapsing the parent hides only its children, not the container row.
    host.querySelector<HTMLElement>('[data-testid="project-shell-twisty-steering-docs"]')!.click();
    fixture.detectChanges();
    expect(railEl(host, 'architecture')).toBeNull();
    expect(railEl(host, 'wiki')).toBeNull();
    expect(railEl(host, 'steering')).toBeNull();
    expect(railEl(host, 'steering-docs')).toBeTruthy();
  });

  it('clicking the non-navigable container toggles children without emitting a rail change', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    let emitted: ProjectRailKey | null = null;
    fixture.componentInstance.railChange.subscribe(k => (emitted = k));

    const containerLabel = railEl(host, 'steering-docs')!;
    containerLabel.click();
    fixture.detectChanges();

    expect(emitted).toBeNull();
    expect(railEl(host, 'architecture')).toBeNull(); // collapsed
  });

  it('renames the former Steering Docs leaf to "Agent Docs" and keeps the steering key', () => {
    const host = mount().nativeElement as HTMLElement;
    const leaf = railEl(host, 'steering')!;
    expect(leaf.textContent).toContain('Agent Docs');
    expect(leaf.textContent).not.toContain('Steering Docs');
  });

  it('exposes Runtime Prompts as its own top-level point in Config', () => {
    const host = mount().nativeElement as HTMLElement;
    const runtime = railEl(host, 'runtime-prompts')!;
    expect(runtime.textContent).toContain('Runtime Prompts');
    // It is not nested under the Steering Docs container (no twisty, top-level row).
    expect(host.querySelector('[data-testid="project-shell-twisty-runtime-prompts"]')).toBeNull();
  });

  it('expands Settings to its grouped sub-pages', () => {
    const host = mount().nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="project-shell-twisty-settings"]')).toBeTruthy();
    expect(railEl(host, 'settings-defaults')!.textContent).toContain('Workspace Defaults');
    expect(railEl(host, 'settings-overrides')!.textContent).toContain('Project Overrides');
  });

  it('renders a text-only rail (no per-item decorative icons)', () => {
    const host = mount().nativeElement as HTMLElement;
    expect(host.querySelector('.proj-shell__rail-icon')).toBeNull();
  });

  // T5a nav-rebuild step 1: the target navigation gains three reachable
  // shells (Pipeline / Workflow / Prompts) at project level. They live in
  // Config and render the generic placeholder panel until step 2 moves real
  // content in.
  it('exposes the Pipeline / Workflow / Prompts shells as reachable Config rows', () => {
    const host = mount().nativeElement as HTMLElement;
    for (const key of ['pipeline', 'workflow', 'prompts']) {
      const row = railEl(host, key);
      expect(row, `rail row ${key}`).toBeTruthy();
      // Top-level points in Config, not nested containers (no twisty).
      expect(host.querySelector(`[data-testid="project-shell-twisty-${key}"]`)).toBeNull();
    }
  });

  it('clicking a new shell row emits its key', () => {
    const fixture = mount('overview');
    const host = fixture.nativeElement as HTMLElement;
    const emitted: ProjectRailKey[] = [];
    fixture.componentInstance.railChange.subscribe(k => emitted.push(k));

    railEl(host, 'pipeline')!.click();
    railEl(host, 'workflow')!.click();
    railEl(host, 'prompts')!.click();
    fixture.detectChanges();

    expect(emitted).toEqual(['pipeline', 'workflow', 'prompts']);
  });

  it('renders the placeholder panel (title + empty hint) for a new shell when active', () => {
    const host = mount('pipeline').nativeElement as HTMLElement;
    const panel = host.querySelector('[data-testid="project-shell-panel-pipeline"]');
    expect(panel).toBeTruthy();
    expect(host.querySelector('[data-testid="project-shell-panel-title"]')!.textContent)
      .toContain('Pipeline');
    expect(host.querySelector('[data-testid="project-shell-panel-empty"]')!.textContent)
      .toContain('Step 2');
  });

  it('clicking a leaf rail emits its key; re-clicking the active rail is a no-op', () => {
    const fixture = mount('overview');
    const host = fixture.nativeElement as HTMLElement;
    const emitted: ProjectRailKey[] = [];
    fixture.componentInstance.railChange.subscribe(k => emitted.push(k));

    railEl(host, 'security')!.click();
    railEl(host, 'overview')!.click(); // already active → no emit
    fixture.detectChanges();

    expect(emitted).toEqual(['security']);
  });
});
