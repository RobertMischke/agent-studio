import { bootstrapApplication } from '@angular/platform-browser';
import { ConversationViewHarnessComponent } from './app/conversation-view-harness.component';

bootstrapApplication(ConversationViewHarnessComponent)
  .catch((err) => console.error(err));
