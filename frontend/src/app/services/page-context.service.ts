import { Injectable, signal } from '@angular/core';
import { Subject } from 'rxjs';
import {
  PageContext,
  PageTaskIntent,
  PageTaskRequest,
  pageContextKey,
} from '../models/page-context.model';

/**
 * App-wide bridge from interactive repository pages to shell-owned actions.
 * Ordinary pages remain navigation details inside the project chat. A
 * Dossier page additionally carries its stable key so route resolution can
 * select the separate persisted Dossier chat.
 */
@Injectable({ providedIn: 'root' })
export class PageContextService {
  readonly activePage = signal<PageContext | null>(null);

  private readonly createTaskSubject = new Subject<PageTaskRequest>();
  readonly createTaskRequests$ = this.createTaskSubject.asObservable();

  private readonly openChatSubject = new Subject<PageContext>();
  readonly openChatRequests$ = this.openChatSubject.asObservable();

  activate(context: PageContext): void {
    this.activePage.set(context);
  }

  clear(expectedKey: string): void {
    const current = this.activePage();
    if (current && pageContextKey(current) === expectedKey) this.activePage.set(null);
  }

  createTask(context: PageContext, intent: PageTaskIntent): void {
    this.activate(context);
    this.createTaskSubject.next({ context, intent });
  }

  openChat(context: PageContext): void {
    this.activate(context);
    this.openChatSubject.next(context);
  }
}
