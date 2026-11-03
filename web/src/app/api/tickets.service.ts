import { HttpClient, httpResource, HttpResourceRef } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { TicketClassification, TicketDetail, TicketListItem, TicketSummary } from './tickets.models';

@Injectable({ providedIn: 'root' })
export class TicketsService {
  private readonly http = inject(HttpClient);

  /** Bumped when something the list shows (a classification) changed on the ticket page, so the list reads itself again. */
  readonly listStale = signal(0);

  /** `GET /api/tickets`, newest first. Call it from an injection context, such as a component field. */
  list(): HttpResourceRef<TicketListItem[] | undefined> {
    return httpResource<TicketListItem[]>(() => '/api/tickets');
  }

  /** `GET /api/tickets/{id}`; reloads when `id` changes and stays idle while it's undefined. A 404 lands in the error state. */
  get(id: () => string | undefined): HttpResourceRef<TicketDetail | undefined> {
    return httpResource<TicketDetail>(() => {
      const value = id();
      return value ? `/api/tickets/${encodeURIComponent(value)}` : undefined;
    });
  }

  /** `POST /api/tickets/{id}/classification`: classifies (or reclassifies) the ticket and returns the saved result. Unsubscribing cancels the request. */
  classify(id: number): Observable<TicketClassification> {
    return this.http.post<TicketClassification>(`/api/tickets/${id}/classification`, null).pipe(tap(() => this.listStale.update((n) => n + 1)));
  }

  /** `POST /api/tickets/{id}/summary`: generates and saves a summary, replacing any earlier one. Errors are problem details. */
  summarize(id: number): Observable<TicketSummary> {
    return this.http.post<TicketSummary>(`/api/tickets/${id}/summary`, null);
  }
}
