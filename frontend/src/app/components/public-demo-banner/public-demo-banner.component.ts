import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NotificationComponent } from '../notification/notification.component';
import { PublicDemoService } from '../../services/public-demo.service';

/**
 * Explains the public demo's read-only boundary (W34 S4).
 *
 * The banner renders only when the server reports the public read-only
 * profile. It states what a visitor can and cannot do so a disabled control
 * reads as a deliberate product boundary rather than a broken button.
 */
@Component({
  selector: 'app-public-demo-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './public-demo-banner.component.html',
  styleUrl: './public-demo-banner.component.scss',
})
export class PublicDemoBannerComponent {
  readonly demo = inject(PublicDemoService);
}
