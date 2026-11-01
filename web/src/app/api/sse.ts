/** One server-sent event: its `event:` name (`message` when the frame has none) and its `data:` lines joined by `\n`. */
export interface SseFrame {
  event: string;
  data: string;
}

/**
 * An incremental parser for `text/event-stream` (the WHATWG rules that matter here). Feed it decoded text as it arrives:
 * a frame can be split across chunks, and one chunk can hold several frames. Lines end with `\n`, `\r\n` or `\r`, even
 * when the `\r` and `\n` arrive in different chunks; `:` lines are comments; a blank line ends a frame, and a frame
 * without data is dropped. `id` and `retry` are ignored, since the client never reconnects.
 */
export class SseParser {
  private buffer = '';
  /** A chunk ended with `\r`; a `\n` at the start of the next one belongs to it. */
  private pendingCr = false;
  private event = '';
  private data: string[] = [];

  /** Parses `chunk` and returns the frames it completed. */
  push(chunk: string): SseFrame[] {
    if (this.pendingCr && chunk.startsWith('\n')) {
      chunk = chunk.slice(1);
    }

    this.pendingCr = false;
    this.buffer += chunk;
    const frames: SseFrame[] = [];
    let start = 0;
    for (let i = 0; i < this.buffer.length; i++) {
      const c = this.buffer[i];
      if (c !== '\n' && c !== '\r') {
        continue;
      }

      const line = this.buffer.slice(start, i);
      if (c === '\r') {
        if (i + 1 === this.buffer.length) {
          this.pendingCr = true;
        } else if (this.buffer[i + 1] === '\n') {
          i++;
        }
      }

      start = i + 1;
      const frame = this.line(line);
      if (frame) {
        frames.push(frame);
      }
    }

    this.buffer = this.buffer.slice(start);
    return frames;
  }

  private line(line: string): SseFrame | undefined {
    if (line === '') {
      return this.dispatch();
    }

    if (line.startsWith(':')) {
      return undefined;
    }

    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'event') {
      this.event = value;
    } else if (field === 'data') {
      this.data.push(value);
    }

    return undefined;
  }

  private dispatch(): SseFrame | undefined {
    const frame = this.data.length > 0 ? { event: this.event || 'message', data: this.data.join('\n') } : undefined;
    this.event = '';
    this.data = [];
    return frame;
  }
}
