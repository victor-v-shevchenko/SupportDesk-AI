import { HttpClient, httpResource, HttpResourceRef } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FollowUps, FollowUpTask } from './follow-ups.models';

@Injectable({ providedIn: 'root' })
export class FollowUpsService {
  private readonly http = inject(HttpClient);

  /** `GET /api/tickets/{id}/follow-ups`; reloads when `ticketId` changes. Call it from an injection context. */
  list(ticketId: () => number): HttpResourceRef<FollowUps | undefined> {
    return httpResource<FollowUps>(() => `/api/tickets/${encodeURIComponent(ticketId())}/follow-ups`);
  }

  /**
   * `POST /api/follow-ups/{proposalId}/confirm`: creates the task, or returns the one an earlier confirm created. Rejects
   * with an `HttpErrorResponse`: `409` when the proposal has expired, `404` when it doesn't exist.
   */
  confirm(proposalId: string): Promise<FollowUpTask> {
    return firstValueFrom(this.http.post<FollowUpTask>(`/api/follow-ups/${encodeURIComponent(proposalId)}/confirm`, null));
  }
}
