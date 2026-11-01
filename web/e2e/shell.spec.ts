import { expect, test } from '@playwright/test';

test('the shell opens on the Tickets page and the API status turns ok', async ({ page }) => {
  await page.goto('/');

  await expect(page).toHaveURL(/\/tickets$/);
  await expect(page.getByRole('link', { name: 'SupportDesk AI' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Tickets', level: 1 })).toBeVisible();
  await expect(page.getByText('API ok')).toBeVisible();
});
