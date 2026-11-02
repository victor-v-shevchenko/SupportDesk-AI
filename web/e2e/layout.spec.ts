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

interface ButtonLook {
  height: number;
  font: string;
  family: string;
  weight: string;
  lineHeight: string;
  padding: string;
  radius: string;
  border: string;
  borderColor: string;
  color: string;
  background: string;
  opacity: string;
}

const pageBackground = { light: 'rgb(255, 255, 255)', dark: 'rgb(15, 20, 25)' };

for (const width of [1440, 375]) {
  for (const scheme of ['light', 'dark'] as const) {
    test.describe(`the action buttons at ${width} px in the ${scheme} theme`, () => {
      test.use({ viewport: { width, height: 900 }, colorScheme: scheme });

      test('Summarize, Classify and Suggest reply share one size and style', async ({ page, request }) => {
        const ticket = await createTicket(request);
        await openTicket(page, ticket.subject);
        // The run really is in this theme, so the colors below are the ones for it
        expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe(pageBackground[scheme]);

        const names = ['Summarize', 'Classify', 'Suggest reply'];
        const looks: ButtonLook[] = await Promise.all(
          names.map(async (name) => {
            const button = page.getByRole('button', { name, exact: true });
            await expect(button).toBeVisible();
            return button.evaluate((el): ButtonLook => {
              const s = getComputedStyle(el);
              return {
                height: Math.round(el.getBoundingClientRect().height),
                font: s.fontSize,
                family: s.fontFamily,
                weight: s.fontWeight,
                lineHeight: s.lineHeight,
                padding: s.padding,
                radius: s.borderRadius,
                border: s.borderTopWidth,
                borderColor: s.borderTopColor,
                color: s.color,
                background: s.backgroundColor,
                opacity: s.opacity,
              };
            });
          }),
        );
        expect(looks[1]).toEqual(looks[0]);
        expect(looks[2]).toEqual(looks[0]);
        await page.screenshot({ path: `${shots}/actions-${width}-${scheme}.png`, fullPage: true });
      });
    });
  }
}
