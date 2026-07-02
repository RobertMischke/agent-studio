import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type {
  ProjectChatDataSource,
  ProjectChatScrollRequest,
  ProjectChatScrollResponse,
  ProjectChatSearchResponse,
  ProjectChatStatsResponse,
  ProjectChatTurnResponse,
} from '@coding-agent/chat/history';
import { TaskService } from './task.service';

/**
 * Host adapter for the `@coding-agent/chat/history` PROJECT_CHAT_DATA_SOURCE
 * seam. The virtualised `<cac-project-chat-list>` decides *when* to load
 * (initial tail, near-top backfill, step-load paging, search, anchored
 * jumps); this adapter answers *how* by delegating to the existing
 * project-chat HTTP endpoints on {@link TaskService}.
 */
@Injectable({ providedIn: 'root' })
export class ProjectChatDataSourceAdapter implements ProjectChatDataSource {
  private readonly tasks = inject(TaskService);

  scroll(project: string, request: ProjectChatScrollRequest): Observable<ProjectChatScrollResponse> {
    return this.tasks.scrollProjectChat(project, request);
  }

  search(project: string, query: string, limit: number): Observable<ProjectChatSearchResponse> {
    return this.tasks.searchProjectChat(project, query, limit);
  }

  stats(project: string): Observable<ProjectChatStatsResponse> {
    return this.tasks.getProjectChatStats(project);
  }

  turn(project: string, turnId: string): Observable<ProjectChatTurnResponse> {
    return this.tasks.getProjectChatTurn(project, turnId);
  }
}
