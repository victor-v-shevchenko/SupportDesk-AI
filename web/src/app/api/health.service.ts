import { httpResource } from '@angular/common/http';
import { Injectable } from '@angular/core';

export interface HealthResponse {
  status: string;
  database: string;
}

@Injectable({ providedIn: 'root' })
export class HealthService {
  /** `GET /api/health`; a 503 (database down) lands in the error state. */
  readonly health = httpResource<HealthResponse>(() => '/api/health');
}
