import { HttpErrorResponse } from '@angular/common/http';

/**
 * What the UI says for each error `code` the API sends (PLAN.md 7 for the reply stream, 9 for problem details). A code the
 * UI doesn't know shows the server's own text.
 */
export const errorMessages: Record<string, string> = {
  model_unavailable: 'The model is unavailable right now. Try again in a moment.',
  timeout: 'The model took too long to answer. Try again.',
  budget_exceeded: 'This ticket is too long for a suggested reply.',
  model_refused: 'The model declined to write a reply for this ticket.',
  invalid_output: "The model's answer was unusable, so nothing was saved. Try again.",
  internal_error: 'Something went wrong while writing the reply. Try again.',
};

/**
 * The message for a failed request: the problem details' `detail` (fixed text written for people, never an exception's
 * message), else the text for its `code`. Undefined when the response has neither, such as a network failure or a 500.
 */
export function problemMessage(error: unknown): string | undefined {
  if (!(error instanceof HttpErrorResponse)) {
    return undefined;
  }

  const body = error.error as { detail?: unknown; code?: unknown } | null;
  if (typeof body?.detail === 'string' && body.detail.length > 0) {
    return body.detail;
  }

  return typeof body?.code === 'string' ? errorMessages[body.code] : undefined;
}
