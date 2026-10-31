import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TicketDetail } from '../api/tickets.models';
import { TicketDetailPage } from './ticket-detail-page';

const ticket: TicketDetail = {
  id: 2,
  number: 'T-1002',
  subject: 'Tent order stuck as delayed',
  body: 'My order was supposed to arrive last week.\nTracking says delayed.',
  customerName: 'Daniel Okafor',
  orderNumber: 'KO-205002',
  status: 'Open',
  createdAt: '2026-10-02T10:31:00Z',
  summary: null,
  classification: { status: 'NotRun', category: null, priority: null, reason: null, promptVersion: null, classifiedAt: null },
};

describe('TicketDetailPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(id = '2') {
    const fixture = TestBed.createComponent(TicketDetailPage);
    fixture.componentRef.setInput('id', id);
    TestBed.tick();
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>) {
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows loading while the request is pending', () => {
    const fixture = create();

    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Loading ticket');
    http.expectOne('/api/tickets/2').flush(ticket);
  });

  it('shows every field and empty slots for summary, classification and reply', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush(ticket);
    const el = await settle(fixture);

    expect(el.querySelector('h1')?.textContent).toContain('T-1002');
    expect(el.querySelector('h1')?.textContent).toContain('Tent order stuck as delayed');
    expect(el.querySelector('.meta')?.textContent).toContain('Daniel Okafor');
    expect(el.querySelector('.order')?.textContent).toBe('KO-205002');
    expect(el.querySelector('app-ticket-status')?.textContent).toBe('Open');
    expect(el.querySelector('.meta time')?.getAttribute('datetime')).toBe('2026-10-02T10:31:00Z');
    expect(el.querySelector('.body')?.textContent).toBe(ticket.body);
    expect(el.querySelector('.summary .empty')?.textContent).toContain('No summary yet');
    expect(el.querySelector('.classification .empty')?.textContent).toContain('Not classified yet');
    expect(el.querySelector('.reply .empty')?.textContent).toContain('No suggested reply yet');
  });

  it('shows None when the ticket has no order number', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush({ ...ticket, orderNumber: null });
    const el = await settle(fixture);

    expect(el.querySelector('.order')?.textContent).toBe('None');
  });

  it('shows a saved summary instead of the empty slot', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush({ ...ticket, summary: { text: 'Customer is waiting on a delayed tent.', promptVersion: 'summary.v1', generatedAt: '2026-10-03T00:00:00Z' } });
    const el = await settle(fixture);

    expect(el.querySelector('.summary')?.textContent).toContain('Customer is waiting on a delayed tent.');
    expect(el.querySelector('.summary .empty')).toBeNull();
  });

  it('says the ticket was not found on a 404, without a retry', async () => {
    const fixture = create('999');
    http.expectOne('/api/tickets/999').flush({ status: 404, title: 'Ticket not found' }, { status: 404, statusText: 'Not Found' });
    const el = await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain('Ticket not found');
    expect(el.querySelector('[role=alert] button')).toBeNull();
  });

  it('shows an error with a retry for other failures', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush(null, { status: 503, statusText: 'Service Unavailable' });
    const el = await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't load this ticket");

    el.querySelector<HTMLButtonElement>('[role=alert] button')?.click();
    TestBed.tick();
    http.expectOne('/api/tickets/2').flush(ticket);
    await fixture.whenStable();

    expect(el.querySelector('h1')?.textContent).toContain('T-1002');
  });
});
