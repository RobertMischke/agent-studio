export { handleBugDirective } from './orchestrator-side-sheet.bug-directive';
export { buildDemoEvents } from './orchestrator-side-sheet.demo-events';
export { readFileAsBase64 } from './orchestrator-side-sheet.file-reader';
export { buildOrchestratorContextEnvelope } from '../../orchestrator-context-envelope';

import type { TaskService } from '../../../../services/task.service';
import { resolveAttachmentUrl } from './orchestrator-side-sheet.util';

export function uploadAttachment(
  jobService: TaskService,
  projectName: string,
  file: File,
): Promise<{ relativePath: string; url: string }> {
  return new Promise((resolve, reject) => {
    jobService.uploadOrchestratorChatAttachment(projectName, file).subscribe({
      next: response => resolve({ relativePath: response.relativePath, url: response.url }),
      error: error => reject(new Error(error?.error?.error || error?.message || 'Upload failed')),
    });
  });
}

export async function preloadPersistedAttachments(
  projectName: string,
  attachments: readonly { relativePath: string }[],
): Promise<void> {
  if (attachments.length === 0) return;
  const loads = attachments.map(attachment => new Promise<void>(resolve => {
    const img = new Image();
    const done = () => resolve();
    img.onload = done;
    img.onerror = done;
    img.src = resolveAttachmentUrl(projectName, attachment.relativePath);
  }));
  await Promise.race([
    Promise.all(loads),
    new Promise<void>(resolve => setTimeout(resolve, 3000)),
  ]);
}
