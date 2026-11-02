import { httpResource, HttpResourceRef } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { ModelCallRow } from './model-calls.models';

@Injectable({ providedIn: 'root' })
export class ModelCallsService {
  /** `GET /api/model-calls?ticketId=`; reloads when `ticketId` changes. Call it from an injection context. */
  forTicket(ticketId: () => number): HttpResourceRef<ModelCallRow[] | undefined> {
    return httpResource<ModelCallRow[]>(() => `/api/model-calls?ticketId=${encodeURIComponent(ticketId())}`);
  }
}
