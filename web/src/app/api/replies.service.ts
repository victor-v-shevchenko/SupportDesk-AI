import { Injectable } from '@angular/core';
import { ReplyEvent, ReplyRequestError, ReplyStreamEndedError } from './replies.models';
import { SseParser } from './sse';

const eventTypes = new Set(['token', 'tool_call', 'citations', 'usage', 'error', 'done']);

@Injectable({ providedIn: 'root' })
export class RepliesService {
  /**
   * `POST /api/tickets/{id}/reply`, read with `fetch` and a `ReadableStream` (not `EventSource`, which reconnects by
   * itself and would run the model again). Yields each event until `done` or `error`. Aborting `signal` cancels the
   * request, and the generator rejects with the fetch's `AbortError`. A stream that ends without `done` or `error`
   * rejects with {@link ReplyStreamEndedError}; a response that isn't a stream rejects with {@link ReplyRequestError}.
   */
  async *stream(ticketId: number, signal: AbortSignal): AsyncGenerator<ReplyEvent> {
    const response = await fetch(`/api/tickets/${encodeURIComponent(ticketId)}/reply`, {
      method: 'POST',
      headers: { Accept: 'text/event-stream' },
      signal,
    });
    if (!response.ok || !response.body) {
      throw new ReplyRequestError(response.status);
    }

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    const parser = new SseParser();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) {
          throw new ReplyStreamEndedError();
        }

        for (const frame of parser.push(value)) {
          if (!eventTypes.has(frame.event)) {
            continue;
          }

          const event = { ...JSON.parse(frame.data), type: frame.event } as ReplyEvent;
          yield event;
          if (event.type === 'done' || event.type === 'error') {
            return;
          }
        }
      }
    } finally {
      // Stops the download when the caller stops early; a no-op once the stream has ended
      reader.cancel().catch(() => undefined);
    }
  }
}
