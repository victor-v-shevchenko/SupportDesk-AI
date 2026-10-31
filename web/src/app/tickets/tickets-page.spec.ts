import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TicketListItem } from '../api/tickets.models';
import { TicketsPage } from './tickets-page';

const tickets: TicketListItem[] = [
  { id: 2, number: 'T-1002', subject: 'Tent order stuck as delayed', status: 'Open', category: null, priority: null, createdAt: '2026-10-02T10:31:00Z' },
  { id: 1, number: 'T-1001', subject: 'How long does shipping take?', status: 'Closed', category: null, priority: null, createdAt: '2026-10-01T08:14:00Z' },
];

describe('TicketsPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create() {
    const fixture = TestBed.createComponent(TicketsPage);
    TestBed.tick();
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>) {
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows loading while the request is pending', () => {
    const fixture = create();

    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Loading tickets');
    http.expectOne('/api/tickets').flush([]);
  });

  it('lists tickets in the order the API returns, linking each to its detail page', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush(tickets);
    const el = await settle(fixture);

    const rows = [...el.querySelectorAll('tbody tr')];
    expect(rows.map((r) => r.querySelector('.number')?.textContent)).toEqual(['T-1002', 'T-1001']);
    const link = rows[0].querySelector('a');
    expect(link?.textContent).toBe('Tent order stuck as delayed');
    expect(link?.getAttribute('href')).toBe('/tickets/2');
    expect(rows[1].querySelector('app-ticket-status')?.textContent).toBe('Closed');
    expect(rows[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-10-02T10:31:00Z');
  });

  it('shows the empty state when there are no tickets', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush([]);
    const el = await settle(fixture);

    expect(el.querySelector('.empty')?.textContent).toContain('No tickets yet');
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows an error with a retry that loads again', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush(null, { status: 500, statusText: 'Server Error' });
    const el = await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't load the tickets");

    el.querySelector<HTMLButtonElement>('[role=alert] button')?.click();
    TestBed.tick();
    http.expectOne('/api/tickets').flush(tickets);
    await fixture.whenStable();

    expect(el.querySelector('[role=alert]')).toBeNull();
    expect(el.querySelectorAll('tbody tr').length).toBe(2);
  });
});
