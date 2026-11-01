import { HttpClient, httpResource, HttpResourceRef } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { TicketClassification, TicketDetail, TicketListItem } from './tickets.models';

@Injectable({ providedIn: 'root' })
export class TicketsService {
  private readonly http = inject(HttpClient);

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

  /** `POST /api/tickets/{id}/classification`: classifies (or reclassifies) the ticket and returns the saved result. */
  classify(id: number): Promise<TicketClassification> {
    return firstValueFrom(this.http.post<TicketClassification>(`/api/tickets/${id}/classification`, null));
  }
}
