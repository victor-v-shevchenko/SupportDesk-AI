/** The events of `POST /api/tickets/{id}/reply` (PLAN.md 7). */

export type ReplyErrorCode = 'model_unavailable' | 'budget_exceeded' | 'model_refused' | 'timeout' | 'internal_error';

export interface Citation {
  marker: number;
  articleSlug: string;
  articleTitle: string;
}

export interface ReplyUsage {
  inputTokens: number;
  outputTokens: number;
  costUsd: number | null;
  estimated: boolean;
}

export type ReplyEvent =
  | { type: 'token'; text: string }
  | { type: 'citations'; citations: Citation[]; removed: number[] }
  | ({ type: 'usage' } & ReplyUsage)
  | { type: 'error'; code: ReplyErrorCode | string; message: string }
  | { type: 'done'; replyRunId: string };

/** The reply request failed before its stream started (an unknown ticket, a server error). */
export class ReplyRequestError extends Error {
  constructor(readonly status: number) {
    super(`The reply request failed with status ${status}.`);
    this.name = 'ReplyRequestError';
  }
}

/** The stream ended without `done` or `error`: the connection dropped or the server stopped. */
export class ReplyStreamEndedError extends Error {
  constructor() {
    super('The reply stream ended before it finished.');
    this.name = 'ReplyStreamEndedError';
  }
}
