import { ChangeDetectionStrategy, Component, effect, input, signal } from '@angular/core';

/**
 * Mirrors CAC's sticky-to-bottom state into a reserved host control row.
 * Keeping the control outside the scroller prevents it from covering turns.
 */
@Component({
  selector: 'app-orchestrator-jump-latest',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-jump-latest.component.html',
  styleUrl: './orchestrator-jump-latest.component.scss',
})
export class OrchestratorJumpLatestComponent {
  readonly conversationHost = input.required<HTMLElement>();
  readonly visible = signal(false);

  constructor() {
    effect((onCleanup) => {
      const host = this.conversationHost();
      if (typeof MutationObserver === 'undefined') return;
      const sync = () => {
        const scroller = host.querySelector<HTMLElement>('[data-testid="conversation-view"]');
        this.visible.set(scroller?.dataset['stuck'] === 'false');
      };
      sync();
      const observer = new MutationObserver(sync);
      observer.observe(host, {
        attributes: true,
        attributeFilter: ['data-stuck'],
        childList: true,
        subtree: true,
      });
      onCleanup(() => observer.disconnect());
    });
  }

  jump(): void {
    this.conversationHost()
      .querySelector<HTMLButtonElement>('[data-testid="conversation-jump-latest"]')
      ?.click();
  }
}
