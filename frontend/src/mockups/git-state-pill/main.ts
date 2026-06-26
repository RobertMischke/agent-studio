import { bootstrapApplication } from '@angular/platform-browser';
import { GitStateGalleryComponent } from './app/git-state-gallery.component';

bootstrapApplication(GitStateGalleryComponent)
  .catch((err) => console.error(err));
