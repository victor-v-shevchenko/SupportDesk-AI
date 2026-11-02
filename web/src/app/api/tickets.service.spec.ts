import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TicketsService } from './tickets.service';

describe('TicketsService', () => {
  let http: HttpTestingController;
  let service: TicketsService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(TicketsService);
  });

  afterEach(() => http.verify());

  it('classify marks the ticket list stale so the list beside the ticket reads itself again', async () => {
    const saved = { status: 'Done', category: 'Other', priority: 'Normal', reason: 'r', promptVersion: 'classify.v2', classifiedAt: '2026-11-01T12:00:00Z' };
    const before = service.listStale();

    const result = service.classify(5);
    http.expectOne('/api/tickets/5/classification').flush(saved);

    expect(await result).toEqual(saved);
    expect(service.listStale()).toBe(before + 1);
  });

  it('classify leaves the list alone when the request fails', async () => {
    const before = service.listStale();

    const result = service.classify(5);
    http.expectOne('/api/tickets/5/classification').flush(null, { status: 503, statusText: 'Unavailable' });

    await expect(result).rejects.toBeDefined();
    expect(service.listStale()).toBe(before);
  });
});
