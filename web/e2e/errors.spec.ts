import { expect, test } from '@playwright/test';

// Failures the fake provider can't produce on demand are played from mocked responses: the page shows a message for
// each error code the API sends (PLAN.md 7 and 9).
const detail = {
  id: 2,
  number: 'T-1002',
  subject: 'Tent order stuck as delayed',
  status: 'Open',
  createdAt: '2026-10-02T10:31:00Z',
  body: 'My order was supposed to arrive last week.',
  customerName: 'Daniel Okafor',
  orderNumber: 'KO-205002',
  summary: null,
  classification: { status: 'NotRun', category: null, priority: null, reason: null, promptVersion: null, classifiedAt: null },
};

function sse(events: [string, unknown][]): string {
  return events.map(([event, data]) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`).join('');
}

test.beforeEach(async ({ page }) => {
  await page.route('/api/tickets', (route) => route.fulfill({ json: [{ ...detail, category: null, priority: null, classificationStatus: 'NotRun' }] }));
  await page.route('/api/tickets/2', (route) => route.fulfill({ json: detail }));
  await page.route('/api/tickets/2/follow-ups', (route) => route.fulfill({ json: { proposals: [], tasks: [] } }));
  await page.route('/api/model-calls?ticketId=2', (route) => route.fulfill({ json: [] }));
});

test('shows an error with a retry when the ticket list fails', async ({ page }) => {
  await page.route('/api/tickets', (route) => route.fulfill({ status: 500 }));

  await page.goto('/tickets');

  await expect(page.getByRole('alert')).toContainText("Couldn't load the tickets");
  await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
});

test('says the ticket was not found on a 404', async ({ page }) => {
  await page.route('/api/tickets/2', (route) => route.fulfill({ status: 404, contentType: 'application/problem+json', body: '{"status":404}' }));

  await page.goto('/tickets/2');

  await expect(page.getByRole('alert').filter({ hasText: 'Ticket not found' })).toBeVisible();
});

const replyErrors: [string, string][] = [
  ['model_unavailable', 'The model is unavailable right now'],
  ['timeout', 'The model took too long to answer'],
  ['budget_exceeded', 'This ticket is too long for a suggested reply'],
  ['model_refused', 'The model declined to write a reply'],
  ['internal_error', 'Something went wrong while writing the reply'],
];

for (const [code, message] of replyErrors) {
  test(`the reply shows a message for the ${code} error`, async ({ page }) => {
    await page.route('/api/tickets/2/reply', (route) =>
      route.fulfill({ contentType: 'text/event-stream', body: sse([['error', { code, message: 'Fixed text.' }]]) }),
    );

    await page.goto('/tickets/2');
    await page.getByRole('button', { name: 'Suggest reply' }).click();

    await expect(page.getByRole('alert')).toContainText(message);
  });
}

const problems: [number, string, string][] = [
  [503, 'model_unavailable', 'The model provider couldn’t be reached.'],
  [422, 'model_refused', 'The model declined to answer for this ticket.'],
  [413, 'budget_exceeded', 'This ticket is too long for the model’s token budget.'],
  [502, 'invalid_output', 'The model’s answer was unusable, so nothing was saved.'],
];

for (const [status, code, text] of problems) {
  test(`summarize and classify show the ${code} problem`, async ({ page }) => {
    const body = JSON.stringify({ status, title: 'Problem', detail: text, code });
    await page.route('/api/tickets/2/summary', (route) => route.fulfill({ status, contentType: 'application/problem+json', body }));
    await page.route('/api/tickets/2/classification', (route) => route.fulfill({ status, contentType: 'application/problem+json', body }));

    await page.goto('/tickets/2');
    await page.getByRole('button', { name: 'Summarize' }).click();
    await expect(page.getByRole('alert')).toContainText(text);

    await page.getByRole('button', { name: 'Classify' }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'classify' })).toContainText(text);
  });
}
