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
  await page.route('/api/tickets/2/follow-ups', (route) => route.fulfill({ json: { proposals: [], tasks: [] } }));
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
  await page.route('/api/tickets/2/follow-ups', (route) => route.fulfill({ json: { proposals: [], tasks: [] } }));
  await page.route('/api/tickets/2/reply', (route) =>
    route.fulfill({ contentType: 'text/event-stream', body: sse([['error', { code: 'model_unavailable', message: 'Down.' }]]) }),
  );

  await page.goto('/tickets/2');
  await page.getByRole('button', { name: 'Suggest reply' }).click();

  await expect(page.getByRole('alert')).toContainText('The model is unavailable right now');
});

test('shows the order and proposal cards and confirms the proposal into a task', async ({ page }) => {
  const proposal = { id: '0196f1a2-0000-7000-8000-000000000001', title: 'Open a carrier trace for KO-205002', notes: 'Still delayed after a week.', dueDate: '2026-11-03' };
  const task = { id: 1, proposalId: proposal.id, title: proposal.title, notes: proposal.notes, dueDate: proposal.dueDate, createdAt: '2026-11-01T12:00:00Z' };
  let confirmed = false;
  await page.route('/api/health', (route) => route.fulfill({ json: { status: 'ok', database: 'ok' } }));
  await page.route('/api/tickets/2', (route) => route.fulfill({ json: detail }));
  await page.route('/api/tickets/2/follow-ups', (route) => route.fulfill({ json: confirmed ? { proposals: [], tasks: [task] } : { proposals: [], tasks: [] } }));
  await page.route(`/api/follow-ups/${proposal.id}/confirm`, (route) => {
    confirmed = true;
    return route.fulfill({ json: task });
  });
  await page.route('/api/tickets/2/reply', (route) =>
    route.fulfill({
      contentType: 'text/event-stream',
      body: sse([
        ['tool_call', { name: 'lookup_order_status', arguments: { orderNumber: 'KO-205002' }, result: { found: true, orderNumber: 'KO-205002', status: 'Delayed', carrier: 'FedEx', lastUpdate: '2026-10-11T07:30:00Z', lastEvent: 'Weather delay at the Memphis hub' } }],
        ['token', { text: 'Your order is delayed by weather. ' }],
        ['tool_call', { name: 'propose_follow_up_task', proposal }],
        ['token', { text: 'An agent will follow up with the carrier.' }],
        ['citations', { citations: [], removed: [] }],
        ['usage', { inputTokens: 2700, outputTokens: 75, costUsd: 0, estimated: false }],
        ['done', { replyRunId: '00000000-0000-0000-0000-000000000001' }],
      ]),
    }),
  );

  await page.goto('/tickets/2');
  await expect(page.getByText('No follow-ups yet.')).toBeVisible();
  await page.getByRole('button', { name: 'Suggest reply' }).click();

  const order = page.locator('app-order-status-card');
  await expect(order).toContainText('Order KO-205002');
  await expect(order).toContainText('Delayed');
  await expect(order).toContainText('Weather delay at the Memphis hub');
  const card = page.locator('.reply app-proposal-card');
  await expect(card).toContainText(proposal.title);
  await expect(card).toContainText('Due Nov 3, 2026');
  await expect(page.locator('.reply-text')).toHaveText('Your order is delayed by weather. An agent will follow up with the carrier.');

  await card.getByRole('button', { name: 'Confirm' }).click();
  await expect(card.getByRole('status')).toHaveText('Task created.');
  await expect(page.locator('.follow-ups .tasks')).toContainText(proposal.title);
});
