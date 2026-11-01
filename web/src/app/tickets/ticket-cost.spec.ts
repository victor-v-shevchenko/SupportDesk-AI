import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ModelCallRow, totalCalls } from '../api/model-calls.models';
import { TicketCost } from './ticket-cost';

const url = '/api/model-calls?ticketId=2';

function row(id: number, inputTokens: number, outputTokens: number, costUsd: number | null, isEstimated = false): ModelCallRow {
  return { id, feature: 'Reply', inputTokens, outputTokens, costUsd, isEstimated };
}

describe('totalCalls', () => {
  it('adds up tokens and known costs', () => {
    const totals = totalCalls([row(1, 1000, 50, 0.0031), row(2, 400, 20, 0.0012)]);

    expect(totals).toEqual({ calls: 2, inputTokens: 1400, outputTokens: 70, costUsd: 0.0043, costIncomplete: false, estimated: false });
  });

  it('marks the cost incomplete when a row has no price, and flags estimates', () => {
    const totals = totalCalls([row(1, 10, 5, null, true), row(2, 10, 5, 0.01)]);

    expect(totals.costUsd).toBe(0.01);
    expect(totals.costIncomplete).toBe(true);
    expect(totals.estimated).toBe(true);
  });
});

describe('TicketCost', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create() {
    const fixture = TestBed.createComponent(TicketCost);
    fixture.componentRef.setInput('ticketId', 2);
    TestBed.tick();
    return fixture;
  }

  it('shows loading while the request is pending', () => {
    const fixture = create();

    expect((fixture.nativeElement as HTMLElement).querySelector('.loading')?.textContent).toContain('Loading usage');
    http.expectOne(url).flush([]);
  });

  it('shows the total tokens, the total cost and the number of calls', async () => {
    const fixture = create();
    http.expectOne(url).flush([row(1, 1000, 50, 0.0031), row(2, 400, 20, 0.0012)]);
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent?.replace(/\s+/g, ' ');
    expect(text).toContain('1,470 tokens');
    expect(text).toContain('(1,400 input + 70 output)');
    expect(text).toContain('$0.0043');
    expect(text).toContain('2 calls');
  });

  it('says so when the ticket has no model calls', async () => {
    const fixture = create();
    http.expectOne(url).flush([]);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.empty')?.textContent).toContain('No model calls yet');
  });

  it('shows an error with a retry', async () => {
    const fixture = create();
    http.expectOne(url).flush(null, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't load the usage");

    el.querySelector<HTMLButtonElement>('[role=alert] button')?.click();
    TestBed.tick();
    http.expectOne(url).flush([row(1, 10, 5, 0.5)]);
    await fixture.whenStable();

    expect(el.querySelector('.totals')?.textContent).toContain('15 tokens');
  });

  it('reads the rows again when refresh changes', async () => {
    const fixture = create();
    http.expectOne(url).flush([]);
    await fixture.whenStable();

    fixture.componentRef.setInput('refresh', 1);
    TestBed.tick();
    http.expectOne(url).flush([row(1, 100, 10, 0.002)]);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('110 tokens');
    fixture.destroy();
  });
});
