import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { FollowUps } from '../api/follow-ups.models';
import { TicketClassification, TicketDetail } from '../api/tickets.models';
import { ReplyPanel } from './reply-panel';
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

const done: TicketClassification = {
  status: 'Done',
  category: 'Shipping',
  priority: 'High',
  reason: 'The order is late and the customer is waiting on it.',
  promptVersion: 'classify.v2',
  classifiedAt: '2026-11-01T12:00:00Z',
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

  /**
   * Renders until stable. The follow-ups list (covered by its own spec) loads once the ticket shows, and the page isn't
   * stable while that request is open, so it's answered here: with no follow-ups unless the test says otherwise.
   */
  async function settle(fixture: ReturnType<typeof create>, followUps: FollowUps = { proposals: [], tasks: [] }) {
    for (let i = 0; i < 3; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      for (const request of http.match((r) => r.url.endsWith('/follow-ups'))) {
        request.flush(followUps);
      }
    }

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

  it('shows badges, the reason, the prompt version and a Reclassify button for a saved classification', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush({ ...ticket, classification: done });
    const el = await settle(fixture);

    const section = el.querySelector('.classification')!;
    expect(section.querySelector('.category')?.textContent).toBe('Shipping');
    expect(section.querySelector('.priority')?.textContent).toBe('High');
    expect(section.querySelector('.badges')?.getAttribute('title')).toBe(done.reason);
    expect(section.querySelector('.reason')?.textContent).toBe(done.reason);
    expect(section.querySelector('.provenance')?.textContent).toContain('classify.v2');
    expect(section.querySelector('button.classify')?.textContent?.trim()).toBe('Reclassify');
  });

  it('shows the needs-manual badge when the model output could not be used', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush({ ...ticket, classification: { ...done, status: 'NeedsManual', category: null, priority: null, reason: null } });
    const el = await settle(fixture);

    const section = el.querySelector('.classification')!;
    expect(section.querySelector('.manual')?.textContent).toBe('Needs manual classification');
    expect(section.querySelector('.category')).toBeNull();
    expect(section.querySelector('button.classify')?.textContent?.trim()).toBe('Reclassify');
  });

  it('classifies with one request while the button is disabled, then shows the result', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush(ticket);
    const el = await settle(fixture);
    const button = el.querySelector<HTMLButtonElement>('button.classify')!;
    expect(button.textContent?.trim()).toBe('Classify');

    button.click();
    button.click();
    TestBed.tick();

    expect(button.disabled).toBe(true);
    expect(el.querySelector('.classification [role=status]')?.textContent).toContain('Classifying');
    const request = http.expectOne('/api/tickets/2/classification');
    expect(request.request.method).toBe('POST');
    request.flush(done);
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    const after = el.querySelector<HTMLButtonElement>('button.classify')!;
    expect(after.disabled).toBe(false);
    expect(el.querySelector('.classification .category')?.textContent).toBe('Shipping');
    expect(after.textContent?.trim()).toBe('Reclassify');
  });

  it('shows the API message on a model error and keeps the saved classification', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush({ ...ticket, classification: done });
    const el = await settle(fixture);

    el.querySelector<HTMLButtonElement>('button.classify')!.click();
    http
      .expectOne('/api/tickets/2/classification')
      .flush({ status: 503, title: 'The model is unavailable', detail: 'Try again in a moment.' }, { status: 503, statusText: 'Service Unavailable' });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    expect(el.querySelector('.classification [role=alert]')?.textContent).toContain("Couldn't classify this ticket: Try again in a moment.");
    expect(el.querySelector('.classification .category')?.textContent).toBe('Shipping');
    expect(el.querySelector<HTMLButtonElement>('button.classify')!.disabled).toBe(false);
  });

  it('shows a newly generated summary without reloading the ticket', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush(ticket);
    const el = await settle(fixture);

    el.querySelector<HTMLButtonElement>('.summary button')?.click();
    http.expectOne('/api/tickets/2/summary').flush({ text: 'The tent order is delayed.', promptVersion: 'summary.v2', generatedAt: '2026-11-01T12:00:00Z' });
    await fixture.whenStable();

    expect(el.querySelector('.summary .text')?.textContent).toBe('The tent order is delayed.');
    expect(el.querySelector('.summary button')?.textContent?.trim()).toBe('Regenerate');
  });

  it('shows the follow-ups list and reloads it when the reply panel says it changed', async () => {
    const fixture = create();
    http.expectOne('/api/tickets/2').flush(ticket);
    let el = await settle(fixture, {
      proposals: [],
      tasks: [{ id: 1, proposalId: 'p1', title: 'Open a carrier trace', notes: '', dueDate: '2026-11-03', createdAt: '2026-11-01T12:00:00Z' }],
    });
    expect(el.querySelector('.follow-ups .tasks')?.textContent).toContain('Open a carrier trace');

    const panel = fixture.debugElement.query(By.directive(ReplyPanel)).componentInstance as ReplyPanel;
    panel.followUpsChanged.emit();
    TestBed.tick();
    http.expectOne('/api/tickets/2/follow-ups').flush({ proposals: [], tasks: [] });

    el = await settle(fixture);
    expect(el.querySelector('.follow-ups .empty')?.textContent).toContain('No follow-ups yet');
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
    await settle(fixture);

    expect(el.querySelector('h1')?.textContent).toContain('T-1002');
  });
});
