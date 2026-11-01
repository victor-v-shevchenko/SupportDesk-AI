import { expect, test } from '@playwright/test';

// The API is mocked here so the test runs without a database or a model; step 11 runs the full stack
const detail = {
  id: 2,
  number: 'T-1002',
  subject: 'Tent order stuck as delayed',
  status: 'Open',
  category: null,
  priority: null,
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

test('suggests a reply with linked citations and its cost', async ({ page }) => {
  await page.route('/api/health', (route) => route.fulfill({ json: { status: 'ok', database: 'ok' } }));
  await page.route('/api/tickets/2', (route) => route.fulfill({ json: detail }));
  await page.route('/api/tickets/2/reply', (route) =>
    route.fulfill({
      contentType: 'text/event-stream',
      body: sse([
        ['token', { text: 'Delayed parcels usually move again ' }],
        ['token', { text: 'within a few days [1]. See [9].' }],
        ['citations', { citations: [{ marker: 1, articleSlug: 'shipping-times', articleTitle: 'Shipping times' }], removed: [9] }],
        ['usage', { inputTokens: 1300, outputTokens: 45, costUsd: 0, estimated: false }],
        ['done', { replyRunId: '00000000-0000-0000-0000-000000000001' }],
      ]),
    }),
  );

  await page.goto('/tickets/2');
  await page.getByRole('button', { name: 'Suggest reply' }).click();

  const reply = page.locator('.reply-text');
  await expect(reply).toHaveText('Delayed parcels usually move again within a few days [1]. See [9].');
  await expect(reply.getByRole('link', { name: '1' })).toHaveAttribute('href', '/articles/shipping-times');
  await expect(reply.getByRole('link')).toHaveCount(1);
  await expect(page.getByRole('link', { name: 'Shipping times' })).toBeVisible();
  await expect(page.getByText('1,300 input + 45 output tokens')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Suggest again' })).toBeVisible();
});

test('shows the error message when the reply fails', async ({ page }) => {
  await page.route('/api/health', (route) => route.fulfill({ json: { status: 'ok', database: 'ok' } }));
  await page.route('/api/tickets/2', (route) => route.fulfill({ json: detail }));
  await page.route('/api/tickets/2/reply', (route) =>
    route.fulfill({ contentType: 'text/event-stream', body: sse([['error', { code: 'model_unavailable', message: 'Down.' }]]) }),
  );

  await page.goto('/tickets/2');
  await page.getByRole('button', { name: 'Suggest reply' }).click();

  await expect(page.getByRole('alert')).toContainText('The model is unavailable right now');
});
