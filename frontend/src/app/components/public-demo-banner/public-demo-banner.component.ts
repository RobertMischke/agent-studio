import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { AuthSessionState } from '../../services/auth.service';

/**
 * App-wide "public read-only demo" banner (AGT-W34 slice S4). Renders whenever
 * the backend reports the public-demo security profile.
 *
 * Its job is explanatory, not protective: the server edge denies every mutation
 * and every route outside the demo allowlist regardless of what the browser
 * shows. Saying so up front is what keeps a disabled control from reading as a
 * broken one. Styled from the semantic notify-info tokens so it tracks both
 * themes.
 */
@Component({
  selector: 'app-public-demo-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './public-demo-banner.component.html',
  styleUrl: './public-demo-banner.component.scss',
})
export class PublicDemoBannerComponent {
  readonly auth = inject(AuthSessionState);
}
