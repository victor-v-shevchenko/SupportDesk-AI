import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TicketSummary } from '../api/tickets.models';
import { GeneratedSummary, TicketSummaryBox } from './ticket-summary';

const saved: TicketSummary = { text: 'Customer is waiting on a delayed tent.', promptVersion: 'summary.v2', generatedAt: '2026-10-03T09:15:00Z' };
const fresh: TicketSummary = { text: 'The tent order is delayed and the customer wants a date.', promptVersion: 'summary.v2', generatedAt: '2026-11-01T12:00:00Z' };

describe('TicketSummaryBox', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function create(summary: TicketSummary | null = null, ticketId = 2) {
    const fixture = TestBed.createComponent(TicketSummaryBox);
    fixture.componentRef.setInput('ticketId', ticketId);
    fixture.componentRef.setInput('summary', summary);
    const emitted: GeneratedSummary[] = [];
    fixture.componentInstance.generated.subscribe((value) => emitted.push(value));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const button = () => el.querySelector<HTMLButtonElement>('button')!;
    return { fixture, el, button, emitted };
  }

  it('offers Summarize and an empty state when there is no summary', async () => {
    const { el, button } = await create();

    expect(button().textContent?.trim()).toBe('Summarize');
    expect(el.querySelector('.empty')?.textContent).toContain('No summary yet');
  });

  it('shows the saved summary with its prompt version and time, and offers Regenerate', async () => {
    const { el, button } = await create(saved);

    expect(el.querySelector('.text')?.textContent).toBe(saved.text);
    expect(el.querySelector('.version')?.textContent).toBe('summary.v2');
    expect(el.querySelector('.generated time')?.getAttribute('datetime')).toBe(saved.generatedAt);
    expect(button().textContent?.trim()).toBe('Regenerate');
    expect(el.querySelector('.empty')).toBeNull();
  });

  it('disables the button and shows loading while the request runs, so two quick clicks send one request', async () => {
    const { fixture, el, button } = await create();

    button().click();
    button().click();
    await fixture.whenStable();

    const request = http.expectOne('/api/tickets/2/summary');
    expect(request.request.method).toBe('POST');
    expect(button().disabled).toBe(true);
    expect(button().textContent?.trim()).toBe('Summarizing…');
    expect(el.querySelector('.loading')?.textContent).toContain('Generating a summary');
    request.flush(fresh);
    await fixture.whenStable();

    expect(button().disabled).toBe(false);
    expect(el.querySelector('.loading')).toBeNull();
  });

  it('emits the new summary with the ticket it was generated for', async () => {
    const { fixture, button, emitted } = await create(saved);

    button().click();
    http.expectOne('/api/tickets/2/summary').flush(fresh);
    await fixture.whenStable();

    expect(emitted).toEqual([{ ticketId: 2, summary: fresh }]);
  });

  it("shows the problem details' message on an error and keeps the saved summary", async () => {
    const { fixture, el, button, emitted } = await create(saved);

    button().click();
    http
      .expectOne('/api/tickets/2/summary')
      .flush(
        { status: 503, title: 'The model is unavailable', detail: "The model provider couldn't be reached or returned an error. Try again in a moment.", code: 'model_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );
    await fixture.whenStable();

    expect(el.querySelector('[role=alert]')?.textContent).toContain("The model provider couldn't be reached");
    expect(el.querySelector('.text')?.textContent).toBe(saved.text);
    expect(button().disabled).toBe(false);
    expect(button().textContent?.trim()).toBe('Regenerate');
    expect(emitted).toEqual([]);
  });

  it('shows a general message when the error has no details, and clears it on the next try', async () => {
    const { fixture, el, button } = await create();

    button().click();
    http.expectOne('/api/tickets/2/summary').flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't generate a summary");

    button().click();
    await fixture.whenStable();
    expect(el.querySelector('[role=alert]')).toBeNull();
    http.expectOne('/api/tickets/2/summary').flush(fresh);
  });

  it('cancels a running request when the ticket changes', async () => {
    const { fixture, button, emitted } = await create();

    button().click();
    const request = http.expectOne('/api/tickets/2/summary');
    fixture.componentRef.setInput('ticketId', 3);
    await fixture.whenStable();

    expect(request.cancelled).toBe(true);
    expect(button().disabled).toBe(false);
    expect(emitted).toEqual([]);
  });

  it('cancels a running request when it is destroyed', async () => {
    const { fixture, button } = await create();

    button().click();
    const request = http.expectOne('/api/tickets/2/summary');
    fixture.destroy();

    expect(request.cancelled).toBe(true);
  });
});
