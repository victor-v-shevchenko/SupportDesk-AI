import { HttpErrorResponse } from '@angular/common/http';
import { errorMessages, problemMessage } from './api-errors';

function failure(body: unknown, status = 503): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('problemMessage', () => {
  it('prefers the problem details text', () => {
    expect(problemMessage(failure({ detail: 'Fixed text.', code: 'timeout' }))).toBe('Fixed text.');
  });

  it('falls back to the text for the code when there is no detail', () => {
    expect(problemMessage(failure({ code: 'invalid_output' }, 502))).toBe(errorMessages['invalid_output']);
  });

  for (const code of Object.keys(errorMessages)) {
    it(`has a message for ${code}`, () => {
      expect(problemMessage(failure({ code }))).toBe(errorMessages[code]);
    });
  }

  it('is undefined for an unknown code, an empty body and errors that are not HTTP responses', () => {
    expect(problemMessage(failure({ code: 'something_new' }))).toBeUndefined();
    expect(problemMessage(failure(null, 500))).toBeUndefined();
    expect(problemMessage(new Error('boom'))).toBeUndefined();
  });
});
