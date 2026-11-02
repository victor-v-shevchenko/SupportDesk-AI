import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ModelCallRow, totalCalls } from '../api/model-calls.models';
import { reloadBackoffMs, TicketCost } from './ticket-cost';

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

    fixture.componentRef.setInput('refresh', { calls: 1 });
    TestBed.tick();
    http.expectOne(url).flush([row(1, 100, 10, 0.002)]);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('110 tokens');
    fixture.destroy();
  });

  describe('after a model call, while its row is not saved yet', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    async function flushNext(body: ModelCallRow[]) {
      TestBed.tick();
      http.expectOne(url).flush(body);
      await vi.advanceTimersByTimeAsync(0);
    }

    it('reads the rows again after each wait of the backoff until a new row shows, then stops', async () => {
      const fixture = create();
      await flushNext([row(1, 100, 10, 0.002)]);

      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext([row(1, 100, 10, 0.002)]); // read at once: the new row is not saved yet

      await vi.advanceTimersByTimeAsync(reloadBackoffMs[0]);
      await flushNext([row(1, 100, 10, 0.002)]); // still not

      await vi.advanceTimersByTimeAsync(reloadBackoffMs[1]);
      await flushNext([row(2, 50, 5, 0.001), row(1, 100, 10, 0.002)]); // there

      expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('2 calls');
      await vi.advanceTimersByTimeAsync(reloadBackoffMs.reduce((a, b) => a + b, 0));
      http.expectNone(url);
      fixture.destroy();
    });

    it('keeps reading while a second call ends before the first call read has finished', async () => {
      const fixture = create();
      await flushNext([]);

      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      // The first read is still loading, so the second call's read was a no-op; it answers with one row
      await flushNext([row(1, 100, 10, 0.002)]);

      await vi.advanceTimersByTimeAsync(reloadBackoffMs[0]);
      await flushNext([row(1, 100, 10, 0.002)]); // still one: the second call's row is not saved yet
      await vi.advanceTimersByTimeAsync(reloadBackoffMs[1]);
      await flushNext([row(2, 50, 5, 0.001), row(1, 100, 10, 0.002)]);

      expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('2 calls');
      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone(url);
      fixture.destroy();
    });

    const one = [row(1, 100, 10, 0.002)];
    const two = [row(2, 50, 5, 0.001), row(1, 100, 10, 0.002)];
    const three = [row(3, 20, 2, 0.0005), ...two];

    /** Lets the backoff run out, answering each read with `body`; returns how many reads it made. */
    async function readsUntilItStops(body: ModelCallRow[]) {
      let reads = 0;
      for (const wait of [...reloadBackoffMs, 60_000]) {
        await vi.advanceTimersByTimeAsync(wait);
        TestBed.tick();
        for (const request of http.match(url)) {
          request.flush(body);
          reads++;
        }
        await vi.advanceTimersByTimeAsync(0);
      }
      return reads;
    }

    it('stops early for a later call after an earlier call wrote no row', async () => {
      const fixture = create();
      await flushNext(one);

      // A call that writes no row (a 404, say): the reads keep returning one row until the readout gives up
      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext(one);
      expect(await readsUntilItStops(one)).toBe(reloadBackoffMs.length);

      // The next call's row is there at once, so there are no more reads
      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext(two);
      expect(await readsUntilItStops(two)).toBe(0);

      expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('2 calls');
      fixture.destroy();
    });

    it('keeps the target when the last read failed, so a later call still waits for its row', async () => {
      const fixture = create();
      await flushNext(one);

      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext(one);
      for (const wait of reloadBackoffMs) {
        await vi.advanceTimersByTimeAsync(wait);
        TestBed.tick();
        const request = http.expectOne(url);
        if (wait === reloadBackoffMs.at(-1)) {
          request.flush(null, { status: 500, statusText: 'Server Error' }); // the last read fails
        } else {
          request.flush(one);
        }
        await vi.advanceTimersByTimeAsync(0);
      }
      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone(url);

      // The next call: its row is not there on the first read, so the readout must read again
      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext(one);
      await vi.advanceTimersByTimeAsync(reloadBackoffMs[0]);
      await flushNext(two);

      expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('2 calls');
      fixture.destroy();
    });

    it('waits for as many rows as a reply with several model calls made, even when the last one lands late', async () => {
      const fixture = create();
      await flushNext([]);

      fixture.componentRef.setInput('refresh', { calls: 3 }); // the reply's usage event said three calls
      TestBed.tick();
      await flushNext(two); // two rows saved at once
      await vi.advanceTimersByTimeAsync(reloadBackoffMs[0]);
      await flushNext(two);
      await vi.advanceTimersByTimeAsync(reloadBackoffMs[1]);
      await flushNext(two); // the third lands about 1.6 s later, past what a "count stopped changing" rule waits
      await vi.advanceTimersByTimeAsync(reloadBackoffMs[2]);
      await flushNext(three);

      expect((fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent).toContain('3 calls');
      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone(url);
      fixture.destroy();
    });

    it('gives up after the last wait of the backoff', async () => {
      const fixture = create();
      await flushNext([row(1, 100, 10, 0.002)]);

      fixture.componentRef.setInput('refresh', { calls: 1 });
      TestBed.tick();
      await flushNext([row(1, 100, 10, 0.002)]);
      for (const wait of reloadBackoffMs) {
        await vi.advanceTimersByTimeAsync(wait);
        await flushNext([row(1, 100, 10, 0.002)]);
      }

      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone(url);
      // The readout keeps showing what the last read returned
      fixture.detectChanges();
      const text = (fixture.nativeElement as HTMLElement).querySelector('.totals')?.textContent?.replace(/\s+/g, ' ');
      expect(text).toContain('1 call');
      fixture.destroy();
    });
  });
});
