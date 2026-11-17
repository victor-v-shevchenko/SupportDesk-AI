import { TestBed } from '@angular/core/testing';
import { ReplyEvent, ReplyRequestError, ReplyStreamEndedError } from './replies.models';
import { RepliesService } from './replies.service';

function streamOf(chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }

      controller.close();
    },
  });
}

async function collect(stream: AsyncGenerator<ReplyEvent>): Promise<ReplyEvent[]> {
  const events: ReplyEvent[] = [];
  for await (const event of stream) {
    events.push(event);
  }

  return events;
}

describe('RepliesService', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('posts to the reply endpoint with the signal and yields events across chunk boundaries', async () => {
    fetchMock.mockResolvedValue(
      new Response(
        streamOf([
          'event: token\ndata: {"text":"Ships "}\n\nevent: tok',
          'en\ndata: {"text":"soon [1]."}\n\nevent: citations\ndata: {"citations":[{"marker":1,"articleSlug":"s","articleTitle":"S"}],"removed":[]}\n\n',
          'event: usage\ndata: {"inputTokens":10,"outputTokens":2,"costUsd":0,"estimated":false,"calls":1}\n\nevent: done\ndata: {"replyRunId":"r1"}\n\n',
        ]),
        { headers: { 'Content-Type': 'text/event-stream' } },
      ),
    );
    const controller = new AbortController();

    const events = await collect(TestBed.inject(RepliesService).stream(2, controller.signal));

    expect(fetchMock).toHaveBeenCalledWith('/api/tickets/2/reply', expect.objectContaining({ method: 'POST', signal: controller.signal }));
    expect(events).toEqual([
      { type: 'token', text: 'Ships ' },
      { type: 'token', text: 'soon [1].' },
      { type: 'citations', citations: [{ marker: 1, articleSlug: 's', articleTitle: 'S' }], removed: [] },
      { type: 'usage', inputTokens: 10, outputTokens: 2, costUsd: 0, estimated: false, calls: 1 },
      { type: 'done', replyRunId: 'r1' },
    ]);
  });

  it('yields tool_call events with their tool name and payload', async () => {
    fetchMock.mockResolvedValue(
      new Response(
        streamOf([
          'event: tool_call\ndata: {"name":"lookup_order_status","arguments":{"orderNumber":"KO-205002"},"result":{"found":false}}\n\n',
          'event: tool_call\ndata: {"name":"propose_follow_up_task","proposal":{"id":"p1","title":"Trace","notes":"","dueDate":null,"status":"Pending"}}\n\n',
          'event: done\ndata: {"replyRunId":"r1"}\n\n',
        ]),
        { headers: { 'Content-Type': 'text/event-stream' } },
      ),
    );

    const events = await collect(TestBed.inject(RepliesService).stream(2, new AbortController().signal));

    expect(events).toEqual([
      { type: 'tool_call', name: 'lookup_order_status', arguments: { orderNumber: 'KO-205002' }, result: { found: false } },
      { type: 'tool_call', name: 'propose_follow_up_task', proposal: { id: 'p1', title: 'Trace', notes: '', dueDate: null, status: 'Pending' } },
      { type: 'done', replyRunId: 'r1' },
    ]);
  });

  it('decodes a character split across two chunks', async () => {
    const bytes = new TextEncoder().encode('event: token\ndata: {"text":"Café"}\n\nevent: done\ndata: {"replyRunId":"r"}\n\n');
    const split = bytes.indexOf(0xc3) + 1;
    fetchMock.mockResolvedValue(
      new Response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(bytes.slice(0, split));
            controller.enqueue(bytes.slice(split));
            controller.close();
          },
        }),
      ),
    );

    const events = await collect(TestBed.inject(RepliesService).stream(2, new AbortController().signal));

    expect(events[0]).toEqual({ type: 'token', text: 'Café' });
  });

  it('stops after an error event', async () => {
    fetchMock.mockResolvedValue(new Response(streamOf(['event: error\ndata: {"code":"timeout","message":"Slow."}\n\nevent: token\ndata: {"text":"x"}\n\n'])));

    const events = await collect(TestBed.inject(RepliesService).stream(2, new AbortController().signal));

    expect(events).toEqual([{ type: 'error', code: 'timeout', message: 'Slow.' }]);
  });

  it('rejects when the stream ends without done or error', async () => {
    fetchMock.mockResolvedValue(new Response(streamOf(['event: token\ndata: {"text":"x"}\n\n'])));

    await expect(collect(TestBed.inject(RepliesService).stream(2, new AbortController().signal))).rejects.toBeInstanceOf(ReplyStreamEndedError);
  });

  it('rejects with the status when the request fails before the stream', async () => {
    fetchMock.mockResolvedValue(new Response('{"title":"Ticket not found"}', { status: 404 }));

    const error = await collect(TestBed.inject(RepliesService).stream(9, new AbortController().signal)).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ReplyRequestError);
    expect((error as ReplyRequestError).status).toBe(404);
  });

  it("carries a refusal's problem detail, or the text for its code when there is none", async () => {
    const problem = (body: object) => new Response(JSON.stringify(body), { status: 429, headers: { 'Content-Type': 'application/problem+json' } });
    fetchMock.mockResolvedValueOnce(problem({ code: 'daily_limit', detail: 'Spent for today.' }));
    fetchMock.mockResolvedValueOnce(problem({ code: 'rate_limited' }));
    fetchMock.mockResolvedValueOnce(new Response('busy', { status: 503 }));
    const service = TestBed.inject(RepliesService);
    const fail = async (id: number): Promise<ReplyRequestError> => {
      const error = await collect(service.stream(id, new AbortController().signal)).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(ReplyRequestError);
      return error as ReplyRequestError;
    };

    expect((await fail(1)).detail).toBe('Spent for today.');
    expect((await fail(2)).detail).toBe('Too many AI requests in a short time. Try again in a moment.');
    const plain = await fail(3);
    expect(plain.status).toBe(503);
    expect(plain.detail).toBeUndefined();
  });
});
