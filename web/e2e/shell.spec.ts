import { expect, test } from '@playwright/test';

test('the shell opens on the Tickets page', async ({ page }) => {
  await page.goto('/');

  await expect(page).toHaveURL(/\/tickets$/);
  await expect(page.getByRole('link', { name: 'SupportDesk AI' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Tickets' })).toBeVisible();
  await expect(page.getByRole('status')).not.toHaveText('Checking API…');
});
