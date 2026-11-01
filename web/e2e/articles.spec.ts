import { expect, test } from '@playwright/test';

// The API is mocked here so the test runs without a database or a model; step 11 runs the full stack
const articles = [
  { slug: 'refunds', title: 'Refunds', updatedAt: '2026-10-31T10:00:00Z' },
  { slug: 'shipping-times', title: 'Shipping times and delivery options', updatedAt: '2026-10-31T10:00:00Z' },
];

const results = [
  { articleSlug: 'refunds', articleTitle: 'Refunds', heading: 'How long refunds take', text: 'Refunds take 5–10 business days.', similarity: 0.7134 },
];

test('searches the knowledge base from the articles page', async ({ page }) => {
  await page.route('/api/health', (route) => route.fulfill({ json: { status: 'ok', database: 'ok' } }));
  await page.route('/api/articles', (route) => route.fulfill({ json: articles }));
  let searched: URL | undefined;
  await page.route('/api/search?*', (route) => {
    searched = new URL(route.request().url());
    return route.fulfill({ json: results });
  });

  await page.goto('/tickets');
  await page.getByRole('link', { name: 'Knowledge base' }).click();
  await expect(page).toHaveURL(/\/articles$/);
  await expect(page.getByText('Shipping times and delivery options')).toBeVisible();

  await page.getByLabel('Question').fill('I sent my boots back and have no money yet');
  await page.getByRole('button', { name: 'Search' }).click();

  await expect(page.getByRole('listitem').filter({ hasText: 'How long refunds take' })).toContainText('0.713');
  expect(searched?.searchParams.get('q')).toBe('I sent my boots back and have no money yet');
  expect(searched?.searchParams.get('k')).toBe('5');
});
