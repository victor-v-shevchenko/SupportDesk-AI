import { expect, Page, test } from '@playwright/test';
import { createTicket, openTicket } from './support';

// The screenshots go to a gitignored folder; the pull request shows them.
const shots = 'e2e-artifacts';

async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

test.describe('at 1440 px wide', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('the list is on the left and the ticket on the right', async ({ page, request }) => {
    const ticket = await createTicket(request);
    await openTicket(page, ticket.subject);
    await page.getByRole('button', { name: 'Suggest reply' }).click();
    await expect(page.getByText('1,200 input + 48 output tokens')).toBeVisible();
    await page.getByRole('button', { name: 'Summarize' }).click();
    await expect(page.getByRole('button', { name: 'Regenerate' })).toBeVisible();

    const list = await page.getByRole('region', { name: 'Tickets' }).boundingBox();
    const detail = await page.getByRole('region', { name: 'Ticket', exact: true }).boundingBox();
    expect(list && detail && list.x + list.width <= detail.x).toBe(true);
    expect(list && detail && Math.abs(list.y - detail.y) < 60).toBe(true);
    await expect(page.getByRole('link', { name: '← All tickets' })).toBeHidden();
    await expectNoHorizontalScroll(page);

    await page.screenshot({ path: `${shots}/ticket-1440.png`, fullPage: true });
  });

  test('the dark theme follows the system setting', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await openTicket(page, /T-1002/);

    const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    expect(background).toBe('rgb(15, 20, 25)');
    await page.screenshot({ path: `${shots}/ticket-1440-dark.png`, fullPage: true });
  });

  test('the knowledge base page', async ({ page }) => {
    await page.goto('/articles');
    await expect(page.getByRole('link', { name: 'Refunds' }).first()).toBeVisible();
    await page.screenshot({ path: `${shots}/articles-1440.png`, fullPage: true });
  });
});

test.describe('at 375 px wide', () => {
  test.use({ viewport: { width: 375, height: 812 } });

  test('the list fills the width, and a ticket replaces it with a way back', async ({ page, request }) => {
    const ticket = await createTicket(request);
    await page.goto('/tickets');
    await expect(page.getByRole('link', { name: ticket.subject })).toBeVisible();
    await expect(page.getByText('Select a ticket to see its details.')).toBeHidden();
    await expectNoHorizontalScroll(page);
    await page.screenshot({ path: `${shots}/list-375.png`, fullPage: true });

    await page.getByRole('link', { name: ticket.subject }).click();
    await expect(page.getByRole('heading', { level: 1 }).filter({ hasText: ticket.number })).toBeVisible();
    await page.getByRole('button', { name: 'Suggest reply' }).click();
    await expect(page.getByText('1,200 input + 48 output tokens')).toBeVisible();
    await page.getByRole('button', { name: 'Summarize' }).click();
    await expect(page.getByRole('button', { name: 'Regenerate' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Tickets' })).toBeHidden();
    await expectNoHorizontalScroll(page);
    await page.screenshot({ path: `${shots}/ticket-375.png`, fullPage: true });

    await page.getByRole('link', { name: '← All tickets' }).click();
    await expect(page.getByRole('heading', { name: 'Tickets', level: 1 })).toBeVisible();
  });

  test('the proposal and order cards fit', async ({ page }) => {
    await openTicket(page, /T-1008/);
    await page.getByRole('button', { name: 'Suggest reply' }).click();
    const reply = page.getByRole('region', { name: 'Suggested reply' });
    await expect(reply.getByRole('heading', { name: 'Order KO-208815' })).toBeVisible();
    await expect(reply.getByRole('button', { name: 'Confirm' })).toBeVisible();
    await expectNoHorizontalScroll(page);
    await page.screenshot({ path: `${shots}/proposal-375.png`, fullPage: true });
  });
});

for (const width of [1440, 375]) {
  for (const scheme of ['light', 'dark'] as const) {
    test.describe(`the action buttons at ${width} px in the ${scheme} theme`, () => {
      test.use({ viewport: { width, height: 900 }, colorScheme: scheme });

      test('Summarize, Reclassify and Suggest reply share one size and style', async ({ page }) => {
        await openTicket(page, /T-1002/);
        const names: (string | RegExp)[] = ['Summarize', /^(Re)?[Cc]lassify$/, 'Suggest reply'];
        const styles = [];
        for (const name of names) {
          const button = page.getByRole('button', { name });
          await expect(button).toBeVisible();
          styles.push(
            await button.evaluate((el) => {
              const s = getComputedStyle(el);
              const box = el.getBoundingClientRect();
              return { height: Math.round(box.height), font: s.fontSize, family: s.fontFamily, padding: s.padding, radius: s.borderRadius, border: s.borderTopWidth };
            }),
          );
        }
        expect(styles[1]).toEqual(styles[0]);
        expect(styles[2]).toEqual(styles[0]);
      });
    });
  }
}
