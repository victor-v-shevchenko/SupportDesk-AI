import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TicketListItem } from '../api/tickets.models';
import { TicketsService } from '../api/tickets.service';
import { TicketsPage } from './tickets-page';

const tickets: TicketListItem[] = [
  { id: 2, number: 'T-1002', subject: 'Tent order stuck as delayed', status: 'Open', category: 'Shipping', priority: 'High', classificationStatus: 'Done', createdAt: '2026-10-02T10:31:00Z' },
  { id: 1, number: 'T-1001', subject: 'How long does shipping take?', status: 'Closed', category: null, priority: null, classificationStatus: 'NeedsManual', createdAt: '2026-10-01T08:14:00Z' },
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

    const rows = [...el.querySelectorAll('.tickets li')];
    expect(rows.map((r) => r.querySelector('.number')?.textContent)).toEqual(['T-1002', 'T-1001']);
    const link = rows[0].querySelector('a');
    expect(link?.querySelector('.subject')?.textContent).toBe('Tent order stuck as delayed');
    expect(link?.getAttribute('href')).toBe('/tickets/2');
    expect(rows[1].querySelector('app-ticket-status')?.textContent).toBe('Closed');
    expect(rows[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-10-02T10:31:00Z');
  });

  it('shows category and priority badges, or the needs-manual badge, in each row', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush([...tickets, { ...tickets[1], id: 9, number: 'T-1009', classificationStatus: 'NotRun' }]);
    const el = await settle(fixture);

    const rows = [...el.querySelectorAll('.tickets li')];
    expect(rows[0].querySelector('.category')?.textContent).toBe('Shipping');
    expect(rows[0].querySelector('.priority')?.textContent).toBe('High');
    expect(rows[1].querySelector('.manual')?.textContent).toBe('Needs manual classification');
    expect(rows[1].querySelector('.category')).toBeNull();
    expect(rows[2].querySelector('.not-run')?.textContent).toBe('Not classified');
  });

  it('asks to select a ticket while none is open', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush(tickets);
    const el = await settle(fixture);

    expect(el.querySelector('.placeholder')?.textContent).toContain('Select a ticket');
    expect(el.querySelector('.split')?.classList.contains('detail-open')).toBe(false);
  });

  it('reads the list again when a ticket was classified, keeping the rows on screen meanwhile', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush(tickets);
    const el = await settle(fixture);

    TestBed.inject(TicketsService).listStale.update((n) => n + 1);
    TestBed.tick();
    const reload = http.expectOne('/api/tickets');
    expect(el.querySelectorAll('.tickets li')).toHaveLength(2);
    reload.flush([{ ...tickets[1], classificationStatus: 'Done', category: 'Returns', priority: 'Low' }, tickets[0]]);
    await fixture.whenStable();

    expect(el.querySelector('.tickets li .category')?.textContent).toBe('Returns');
  });

  it('shows the empty state when there are no tickets', async () => {
    const fixture = create();
    http.expectOne('/api/tickets').flush([]);
    const el = await settle(fixture);

    expect(el.querySelector('.empty')?.textContent).toContain('No tickets yet');
    expect(el.querySelector('.tickets')).toBeNull();
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
    expect(el.querySelectorAll('.tickets li')).toHaveLength(2);
  });
});
