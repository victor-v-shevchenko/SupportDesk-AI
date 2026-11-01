import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ReplyEvent, ReplyRequestError, ReplyStreamEndedError } from '../api/replies.models';
import { RepliesService } from '../api/replies.service';
import { ReplyPanel, replyErrorMessages } from './reply-panel';

/** One scripted reply request: the test pushes events, and aborting the signal rejects like `fetch` does. */
class ScriptedStream {
  private readonly queue: ({ event: ReplyEvent } | { error: unknown })[] = [];
  private wake: (() => void) | undefined;

  constructor(readonly ticketId: number, readonly signal: AbortSignal) {
    signal.addEventListener('abort', () => this.wake?.());
  }

  push(...events: ReplyEvent[]): void {
    this.queue.push(...events.map((event) => ({ event })));
    this.wake?.();
  }

  fail(error: unknown): void {
    this.queue.push({ error });
    this.wake?.();
  }

  async *run(): AsyncGenerator<ReplyEvent> {
    while (true) {
      if (this.signal.aborted) {
        throw new DOMException('The operation was aborted.', 'AbortError');
      }

      const next = this.queue.shift();
      if (!next) {
        await new Promise<void>((resolve) => (this.wake = resolve));
        continue;
      }

      if ('error' in next) {
        throw next.error;
      }

      yield next.event;
      if (next.event.type === 'done' || next.event.type === 'error') {
        return;
      }
    }
  }
}

describe('ReplyPanel', () => {
  let streams: ScriptedStream[];

  beforeEach(() => {
    streams = [];
    const fake: Pick<RepliesService, 'stream'> = {
      stream: (ticketId, signal) => {
        const stream = new ScriptedStream(ticketId, signal);
        streams.push(stream);
        return stream.run();
      },
    };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: RepliesService, useValue: fake }],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  function create() {
    const fixture = TestBed.createComponent(ReplyPanel);
    fixture.componentRef.setInput('ticketId', 2);
    fixture.detectChanges();
    return fixture;
  }

  async function settle(fixture: ReturnType<typeof create>) {
    // Lets the panel's for-await loop take what was pushed, then renders
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
    }

    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function click(el: HTMLElement, selector: string) {
    el.querySelector<HTMLButtonElement>(selector)!.click();
  }

  it('starts empty with a Suggest reply button', () => {
    const el = create().nativeElement as HTMLElement;

    expect(el.querySelector('.suggest')?.textContent).toContain('Suggest reply');
    expect(el.querySelector('.empty')?.textContent).toContain('No suggested reply yet');
    expect(el.querySelector('.stop')).toBeNull();
  });

  it('shows loading, then the text as it streams, then links citations and shows the cost', async () => {
    const fixture = create();
    let el = fixture.nativeElement as HTMLElement;
    click(el, '.suggest');
    el = await settle(fixture);

    expect(streams[0].ticketId).toBe(2);
    expect(el.querySelector('.loading')?.textContent).toContain('Writing a reply');
    expect(el.querySelector('.stop')).not.toBeNull();

    streams[0].push({ type: 'token', text: 'Ships in 3 days [1]' });
    el = await settle(fixture);
    expect(el.querySelector('.reply-text')?.textContent).toBe('Ships in 3 days [1]');
    expect(el.querySelector('.reply-text a')).toBeNull();

    streams[0].push(
      { type: 'token', text: ', not [7].' },
      { type: 'citations', citations: [{ marker: 1, articleSlug: 'shipping-times', articleTitle: 'Shipping times' }], removed: [7] },
      { type: 'usage', inputTokens: 1200, outputTokens: 48, costUsd: 0.0012, estimated: false },
      { type: 'done', replyRunId: 'r1' },
    );
    el = await settle(fixture);

    expect(el.querySelector('.reply-text')?.textContent).toBe('Ships in 3 days [1], not [7].');
    const links = el.querySelectorAll<HTMLAnchorElement>('.reply-text a');
    expect(links).toHaveLength(1);
    expect(links[0].textContent).toBe('1');
    expect(links[0].getAttribute('href')).toBe('/articles/shipping-times');
    expect(el.querySelector('.citations')?.textContent).toContain('Shipping times');
    expect(el.querySelector('.citations a')?.getAttribute('href')).toBe('/articles/shipping-times');
    expect(el.querySelector('.usage')?.textContent).toContain('1,200 input + 48 output tokens');
    expect(el.querySelector('.usage')?.textContent).toContain('$0.0012');
    expect(el.querySelector('.stop')).toBeNull();
    expect(el.querySelector('.suggest')?.textContent).toContain('Suggest again');
  });

  it('shows an order card and a proposal card in the order the tools ran, above the reply text', async () => {
    const fixture = create();
    let changes = 0;
    fixture.componentInstance.followUpsChanged.subscribe(() => changes++);
    click(fixture.nativeElement, '.suggest');
    streams[0].push(
      { type: 'tool_call', name: 'lookup_order_status', arguments: { orderNumber: 'KO-208815' }, result: { found: false } },
      { type: 'token', text: "I couldn't find that order. " },
      { type: 'tool_call', name: 'propose_follow_up_task', proposal: { id: 'p1', title: 'Trace the parcel', notes: '', dueDate: null, status: 'Pending' } },
    );
    let el = await settle(fixture);

    const cards = [...el.querySelectorAll('.tool-calls > *')].map((c) => c.tagName.toLowerCase());
    expect(cards).toEqual(['app-order-status-card', 'app-proposal-card']);
    expect(el.querySelector('app-order-status-card')?.textContent).toContain('KO-208815');
    expect(el.querySelector('app-order-status-card .not-found')).not.toBeNull();
    expect(el.querySelector('.tool-calls')!.compareDocumentPosition(el.querySelector('.reply-text')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(changes).toBe(1);

    // A repeated proposal in the same run is the same saved proposal: its card is updated, not doubled
    streams[0].push(
      { type: 'tool_call', name: 'propose_follow_up_task', proposal: { id: 'p1', title: 'Open a carrier trace', notes: 'Three weeks late.', dueDate: '2026-11-03', status: 'Pending' } },
      { type: 'done', replyRunId: 'r1' },
    );
    el = await settle(fixture);
    const proposals = el.querySelectorAll('app-proposal-card');
    expect(proposals).toHaveLength(1);
    expect(proposals[0].querySelector('.title')?.textContent).toBe('Open a carrier trace');
    expect(changes).toBe(2);
  });

  it('confirming a proposal from the panel tells the page its follow-ups changed', async () => {
    const fixture = create();
    let changes = 0;
    fixture.componentInstance.followUpsChanged.subscribe(() => changes++);
    click(fixture.nativeElement, '.suggest');
    streams[0].push(
      { type: 'tool_call', name: 'propose_follow_up_task', proposal: { id: 'p1', title: 'Trace the parcel', notes: '', dueDate: null, status: 'Pending' } },
      { type: 'done', replyRunId: 'r1' },
    );
    let el = await settle(fixture);

    click(el, 'app-proposal-card .confirm');
    TestBed.inject(HttpTestingController)
      .expectOne('/api/follow-ups/p1/confirm')
      .flush({ id: 1, proposalId: 'p1', title: 'Trace the parcel', notes: '', dueDate: null, createdAt: '2026-11-01T12:00:00Z' });
    el = await settle(fixture);
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();

    expect(el.querySelector('app-proposal-card .confirmed')?.textContent).toContain('Task created');
    expect(changes).toBe(2);
  });

  it('shows a proposal the server reports as already confirmed without a Confirm button', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push({ type: 'tool_call', name: 'propose_follow_up_task', proposal: { id: 'p1', title: 'Trace', notes: '', dueDate: null, status: 'Confirmed' } });
    const el = await settle(fixture);

    expect(el.querySelector('app-proposal-card .confirmed')).not.toBeNull();
    expect(el.querySelector('app-proposal-card .confirm')).toBeNull();
  });

  it('clears the cards when a new reply starts', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push({ type: 'tool_call', name: 'lookup_order_status', arguments: { orderNumber: 'KO-205002' }, result: { found: false } });
    let el = await settle(fixture);
    expect(el.querySelector('app-order-status-card')).not.toBeNull();

    click(el, '.suggest');
    el = await settle(fixture);

    expect(el.querySelector('.tool-calls')).toBeNull();
  });

  it('marks estimated usage and an unknown cost', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push(
      { type: 'token', text: 'Hi.' },
      { type: 'citations', citations: [], removed: [] },
      { type: 'usage', inputTokens: 5, outputTokens: 1, costUsd: null, estimated: true },
      { type: 'done', replyRunId: 'r1' },
    );
    const el = await settle(fixture);

    expect(el.querySelector('.usage')?.textContent).toContain('(estimated)');
    expect(el.querySelector('.usage')?.textContent).toContain('cost unknown');
    expect(el.querySelector('.no-citations')?.textContent).toContain('No sources cited');
  });

  for (const code of Object.keys(replyErrorMessages)) {
    it(`shows a readable message for ${code}`, async () => {
      const fixture = create();
      click(fixture.nativeElement, '.suggest');
      streams[0].push({ type: 'token', text: 'Partial' }, { type: 'error', code, message: 'server text' });
      const el = await settle(fixture);

      expect(el.querySelector('[role=alert]')?.textContent).toContain(replyErrorMessages[code]);
      expect(el.querySelector('.reply-text')?.textContent).toBe('Partial');
      expect(el.querySelector('.stop')).toBeNull();
    });
  }

  it("falls back to the server's message for an unknown code", async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push({ type: 'error', code: 'something_new', message: 'Something new happened.' });
    const el = await settle(fixture);

    expect(el.querySelector('[role=alert]')?.textContent).toContain('Something new happened.');
  });

  it('explains a request that failed or a stream that dropped', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].fail(new ReplyRequestError(404));
    let el = await settle(fixture);
    expect(el.querySelector('[role=alert]')?.textContent).toContain('This ticket no longer exists');

    click(el, '.suggest');
    streams[1].fail(new ReplyStreamEndedError());
    el = await settle(fixture);
    expect(el.querySelector('[role=alert]')?.textContent).toContain('connection dropped');

    click(el, '.suggest');
    streams[2].fail(new TypeError('Failed to fetch'));
    el = await settle(fixture);
    expect(el.querySelector('[role=alert]')?.textContent).toContain("Couldn't reach the server");
  });

  it('stop aborts the request and keeps the text so far', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push({ type: 'token', text: 'Half a reply' });
    let el = await settle(fixture);

    click(el, '.stop');
    el = await settle(fixture);

    expect(streams[0].signal.aborted).toBe(true);
    expect(el.querySelector('.reply-text')?.textContent).toBe('Half a reply');
    expect(el.querySelector('.note')?.textContent).toContain('Stopped');
    expect(el.querySelector('[role=alert]')).toBeNull();
  });

  it('a second click aborts the first request and shows only the second reply', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    streams[0].push({ type: 'token', text: 'First' });
    let el = await settle(fixture);

    click(el, '.suggest');
    streams[0].push({ type: 'token', text: ' late' });
    streams[1].push({ type: 'token', text: 'Second' });
    el = await settle(fixture);

    expect(streams).toHaveLength(2);
    expect(streams[0].signal.aborted).toBe(true);
    expect(streams[1].signal.aborted).toBe(false);
    expect(el.querySelector('.reply-text')?.textContent).toBe('Second');
    expect(el.querySelector('[role=alert]')).toBeNull();
  });

  it('leaving the page aborts the request', async () => {
    const fixture = create();
    click(fixture.nativeElement, '.suggest');
    await settle(fixture);

    fixture.destroy();

    expect(streams[0].signal.aborted).toBe(true);
  });
});
