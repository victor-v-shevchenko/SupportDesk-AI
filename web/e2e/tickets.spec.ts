import { expect, test } from '@playwright/test';

// The API is mocked here so the test runs without a database; step 11 runs the full stack
const list = [
  { id: 2, number: 'T-1002', subject: 'Tent order stuck as delayed', status: 'Open', category: null, priority: null, createdAt: '2026-10-02T10:31:00Z' },
  { id: 1, number: 'T-1001', subject: 'How long does standard shipping take?', status: 'Closed', category: null, priority: null, createdAt: '2026-10-01T08:14:00Z' },
];

const detail = {
  ...list[0],
  body: 'My order was supposed to arrive last week.',
  customerName: 'Daniel Okafor',
  orderNumber: 'KO-205002',
  summary: null,
  classification: { status: 'NotRun', category: null, priority: null, reason: null, promptVersion: null, classifiedAt: null },
};

test('opens a ticket from the list', async ({ page }) => {
  await page.route('/api/health', (route) => route.fulfill({ json: { status: 'ok', database: 'ok' } }));
  await page.route('/api/tickets', (route) => route.fulfill({ json: list }));
  await page.route('/api/tickets/2', (route) => route.fulfill({ json: detail }));

  await page.goto('/tickets');
  await expect(page.getByRole('row')).toHaveCount(3);
  await page.getByRole('link', { name: 'Tent order stuck as delayed' }).click();

  await expect(page).toHaveURL(/\/tickets\/2$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('T-1002');
  await expect(page.getByText('Daniel Okafor')).toBeVisible();
  await expect(page.getByText('No summary yet.')).toBeVisible();
});

test('shows an error when the ticket list fails', async ({ page }) => {
  await page.route('/api/tickets', (route) => route.fulfill({ status: 500 }));

  await page.goto('/tickets');

  await expect(page.getByRole('alert')).toContainText("Couldn't load the tickets");
});
