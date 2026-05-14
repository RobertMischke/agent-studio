import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
} from '@angular/core';
import { MediaLightboxService } from '../../services/media-lightbox.service';
import { ModalStackService } from '../../services/modal-stack.service';

/**
 * Single-instance image lightbox. Mounted once at the app shell so every
 * markdown surface (task description, activity-log, chat, info-button,
 * results) opens the same overlay through `MediaLightboxService`.
 *
 * Behaviour:
 *  - Backdrop click closes.
 *  - Close button closes.
 *  - Escape closes (via `ModalStackService` so a confirm dialog above the
 *    lightbox still wins).
 *  - Large images are constrained to the viewport but allow zoom-to-actual
 *    via the click-on-image affordance (toggles `object-fit: contain`
 *    vs intrinsic size + scroll container).
 */
@Component({
  selector: 'app-media-lightbox',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './media-lightbox.component.html',
  styleUrl: './media-lightbox.component.scss',
})
export class MediaLightboxComponent {
  readonly lightbox = inject(MediaLightboxService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  private stackDispose: (() => void) | null = null;

  constructor() {
    effect(() => {
      const open = this.lightbox.active() !== null;
      if (open) {
        if (!this.stackDispose) {
          this.stackDispose = this.modalStack.push('media-lightbox', () =>
            this.lightbox.close()
          );
        }
      } else if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      if (this.stackDispose) {
        this.stackDispose();
        this.stackDispose = null;
      }
    });
  }

  close(): void {
    this.lightbox.close();
  }
}
