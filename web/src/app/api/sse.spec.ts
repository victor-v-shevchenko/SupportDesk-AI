import { SseParser } from './sse';

describe('SseParser', () => {
  it('parses a frame with an event name and JSON data', () => {
    const parser = new SseParser();

    expect(parser.push('event: token\ndata: {"text":"Hi"}\n\n')).toEqual([{ event: 'token', data: '{"text":"Hi"}' }]);
  });

  it('waits for the rest of a frame split across two chunks', () => {
    const parser = new SseParser();

    expect(parser.push('event: tok')).toEqual([]);
    expect(parser.push('en\ndata: {"text":')).toEqual([]);
    expect(parser.push('"Hi"}\n')).toEqual([]);
    expect(parser.push('\n')).toEqual([{ event: 'token', data: '{"text":"Hi"}' }]);
  });

  it('returns every frame in a chunk that holds several, and keeps the partial one', () => {
    const parser = new SseParser();

    const frames = parser.push('event: token\ndata: {"text":"a"}\n\nevent: token\ndata: {"text":"b"}\n\nevent: done\ndata: {"rep');

    expect(frames).toEqual([
      { event: 'token', data: '{"text":"a"}' },
      { event: 'token', data: '{"text":"b"}' },
    ]);
    expect(parser.push('lyRunId":"x"}\n\n')).toEqual([{ event: 'done', data: '{"replyRunId":"x"}' }]);
  });

  it('accepts CRLF and CR line endings, including a CRLF split between chunks', () => {
    const parser = new SseParser();

    expect(parser.push('event: a\r\ndata: 1\r')).toEqual([]);
    expect(parser.push('\n\r')).toEqual([{ event: 'a', data: '1' }]);
    expect(parser.push('event: b\rdata: 2\r\r')).toEqual([{ event: 'b', data: '2' }]);
  });

  it('joins data lines, skips comments and frames without data, and defaults the event name', () => {
    const parser = new SseParser();

    const frames = parser.push(': keep-alive\n\nevent: ignored\n\ndata: line one\ndata:line two\nid: 7\n\n');

    expect(frames).toEqual([{ event: 'message', data: 'line one\nline two' }]);
  });

  it('keeps a space that is part of the value', () => {
    const parser = new SseParser();

    expect(parser.push('data:  two spaces\n\n')).toEqual([{ event: 'message', data: ' two spaces' }]);
  });
});
