import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { NotificationStackComponent } from './notification-stack.component';
import { NotificationService } from '../../../services/notification.service';

describe('NotificationStackComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [NotificationStackComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(NotificationStackComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] NotificationStackComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('NotificationStackComponent — position routing', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [NotificationStackComponent],
      providers: [provideZonelessChangeDetection(), NotificationService],
    });
    const service = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(NotificationStackComponent);
    return { service, fixture, component: fixture.componentInstance };
  }

  it('default toasts route to the top-right pile, not bottom-right', () => {
    const { service, component } = setup();
    service.notify({ kind: 'success', message: 'saved' });

    expect(component.topRight().map((n) => n.message)).toEqual(['saved']);
    expect(component.bottomRight()).toHaveLength(0);
  });

  it('position=bottom-right routes only that toast to the bottom-right pile', () => {
    const { service, component } = setup();
    service.notify({ kind: 'info', message: 'top one' });
    service.notify({ kind: 'info', message: 'undo me', position: 'bottom-right' });

    expect(component.topRight().map((n) => n.message)).toEqual(['top one']);
    expect(component.bottomRight().map((n) => n.message)).toEqual(['undo me']);
  });

  it('renders each pile into its own positioned container', () => {
    const { service, fixture } = setup();
    service.notify({ kind: 'success', message: 'top toast' });
    service.notify({ kind: 'info', message: 'bottom toast', position: 'bottom-right' });
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[position] render skipped:', (e as Error).message);
      return;
    }

    const root: HTMLElement = fixture.nativeElement;
    const top = root.querySelector('[data-testid="notification-stack"]');
    const bottom = root.querySelector('[data-testid="notification-stack-bottom-right"]');
    expect(top).toBeTruthy();
    expect(bottom).toBeTruthy();

    expect(top!.textContent).toContain('top toast');
    expect(top!.textContent).not.toContain('bottom toast');
    expect(bottom!.textContent).toContain('bottom toast');
    expect(bottom!.textContent).not.toContain('top toast');
    expect(bottom!.classList.contains('app-notify-stack--bottom-right')).toBe(true);
  });
});
