import { expect, test } from '@playwright/test';
import { createTicket, openTicket, readCost } from './support';

// The full stack: the API with the fake provider (scenarios are keyed by the seeded tickets' numbers, PLAN.md 6.8) and
// the Angular app. Tests that need no scenario make their own ticket. The scenario tests share seeded tickets and
// assert only on what their own run produced (their own proposal, the visible text), so they can run in any order:
//   T-1002 reply (order card)   T-1008 reply and confirm (proposals, tasks)   T-1019 reply, stopped
//   T-1021 reply (error)        T-1024 classification (NeedsManual)

test('opens the list, opens a ticket and shows its details', async ({ page }) => {
  await page.goto('/tickets');
  await expect(page.getByRole('heading', { name: 'Tickets', level: 1 })).toBeVisible();
  await expect(page.getByText('Select a ticket to see its details.')).toBeVisible();

  await page.getByRole('link', { name: /T-1002/ }).click();

  await expect(page).toHaveURL(/\/tickets\/2$/);
  await expect(page.getByRole('heading', { level: 1 }).filter({ hasText: 'T-1002' })).toContainText('Tent order stuck as delayed');
  await expect(page.getByText('Daniel Okafor')).toBeVisible();
  await expect(page.getByText('KO-205002')).toBeVisible();
  await expect(page.getByText('No summary yet.')).toBeVisible();
  // The list stays beside the ticket, with the open ticket marked
  await expect(page.getByRole('link', { name: /T-1002/ })).toHaveAttribute('aria-current', 'page');
});

test('suggests a reply: text streams in, citations link to articles, the cost line shows', async ({ page, request }) => {
  const ticket = await createTicket(request);
  await openTicket(page, ticket.subject);
  const reply = page.getByRole('region', { name: 'Suggested reply' });

  await reply.getByRole('button', { name: 'Suggest reply' }).click();

  await expect(reply.getByText(/Standard shipping takes 3 to 5 business days/)).toBeVisible();
  const cite = reply.getByRole('paragraph').getByRole('link', { name: '1' });
  await expect(cite).toBeVisible();
  await expect(cite).toHaveAttribute('href', /^\/articles\/[\w-]+$/);
  const source = reply.getByRole('list').getByRole('link');
  await expect(source).toBeVisible();
  await expect(reply.getByText('1,200 input + 48 output tokens')).toBeVisible();

  // The citation leads to the article
  await cite.click();
  await expect(page).toHaveURL(/\/articles\/[\w-]+$/);
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});

test('the ticket cost readout matches the sum of its model call rows', async ({ page, request }) => {
  const ticket = await createTicket(request);
  await openTicket(page, ticket.subject);
  await expect(page.getByText('No model calls yet.')).toBeVisible();

  await page.getByRole('button', { name: 'Suggest reply' }).click();
  await page.getByRole('button', { name: 'Summarize' }).click();
  await expect(page.getByRole('button', { name: 'Regenerate' })).toBeVisible();

  // The usage rows are saved in the background; wait until the readout shows both calls
  await expect(page.locator('.cost .totals')).toContainText('2 calls');
  const rows = (await (await request.get(`/api/model-calls?ticketId=${ticket.id}`)).json()) as {
    inputTokens: number;
    outputTokens: number;
    costUsd: number | null;
  }[];
  expect(rows).toHaveLength(2);
  const shown = await readCost(page);
  const cost = rows.reduce((sum, r) => sum + (r.costUsd ?? 0), 0);
  // The e2e API prices the fake model (web/playwright.config.ts), so the dollars are not 0
  expect(cost).toBeGreaterThan(0);
  expect(shown.input).toBe(rows.reduce((sum, r) => sum + r.inputTokens, 0));
  expect(shown.output).toBe(rows.reduce((sum, r) => sum + r.outputTokens, 0));
  expect(shown.calls).toBe(2);
  expect(shown.costUsd).toBeCloseTo(cost, 5);
});

test('stops a reply mid-stream and starts a new one', async ({ page }) => {
  // T-1019 writes a sentence and then stalls, so the reply is still streaming when Stop is clicked
  await openTicket(page, /T-1019/);
  const reply = page.getByRole('region', { name: 'Suggested reply' });

  await reply.getByRole('button', { name: 'Suggest reply' }).click();
  await expect(reply.getByText('Sorry about the checkout error.')).toBeVisible();
  await reply.getByRole('button', { name: 'Stop' }).click();

  await expect(reply.getByText('Stopped.')).toBeVisible();
  await expect(reply.getByText('Sorry about the checkout error.')).toBeVisible();
  await expect(reply.getByRole('button', { name: 'Stop' })).toHaveCount(0);

  await reply.getByRole('button', { name: 'Suggest again' }).click();
  await expect(reply.getByText('Stopped.')).toHaveCount(0);
  await expect(reply.getByRole('button', { name: 'Stop' })).toBeVisible();
  await expect(reply.getByText('Sorry about the checkout error.')).toBeVisible();
  await reply.getByRole('button', { name: 'Stop' }).click();
  await expect(reply.getByText('Stopped.')).toBeVisible();
});

test('a reply with an order lookup shows the order card', async ({ page }) => {
  await openTicket(page, /T-1002/);
  const reply = page.getByRole('region', { name: 'Suggested reply' });

  await reply.getByRole('button', { name: 'Suggest reply' }).click();

  const card = reply.getByRole('heading', { name: 'Order KO-205002' }).locator('..');
  await expect(card).toBeVisible();
  await expect(card).toContainText('Delayed');
  await expect(card).toContainText('Carrier');
  await expect(reply.getByText(/I checked order KO-205002/)).toBeVisible();
});

test('a reply with a proposal: confirming creates one task, confirming again does not add another', async ({ page }) => {
  await openTicket(page, /T-1008/);
  const ticketId = /\/tickets\/(\d+)$/.exec(page.url())?.[1];
  expect(ticketId).toBeTruthy();
  const reply = page.getByRole('region', { name: 'Suggested reply' });
  const title = 'Open a carrier trace for KO-208815';

  await reply.getByRole('button', { name: 'Suggest reply' }).click();

  // This run's proposal is the card in the reply; other tests may have proposed the same title on this ticket
  const card = reply.getByRole('heading', { name: title }).locator('..');
  await expect(card).toBeVisible();
  await expect(reply.getByText(/If the number is right, an agent can check with the carrier/)).toBeVisible();
  const confirmed = page.waitForResponse((r) => /\/api\/follow-ups\/[^/]+\/confirm$/.test(r.url()) && r.request().method() === 'POST');
  await card.getByRole('button', { name: 'Confirm' }).click();
  const confirmUrl = (await confirmed).url();
  await expect(card.getByText('Task created.')).toBeVisible();

  const proposalId = /\/follow-ups\/([^/]+)\/confirm$/.exec(confirmUrl)?.[1];
  const tasksFor = async () => {
    const body = (await (await page.request.get(`/api/tickets/${ticketId}/follow-ups`)).json()) as { tasks: { proposalId: string }[] };
    return body.tasks.filter((t) => t.proposalId === proposalId).length;
  };
  expect(await tasksFor()).toBe(1);

  // A second confirm of the same proposal (here through the API, as a double click or a second tab would) adds nothing
  const again = await page.request.post(confirmUrl);
  expect(again.ok()).toBe(true);
  expect(await tasksFor()).toBe(1);
});

test('a model error before the first item shows the model unavailable message', async ({ page }) => {
  test.setTimeout(45_000);
  // T-1021 fails with a 503 on every attempt; the pipeline retries before the error reaches the page
  await openTicket(page, /T-1021/);
  const reply = page.getByRole('region', { name: 'Suggested reply' });

  await reply.getByRole('button', { name: 'Suggest reply' }).click();

  await expect(reply.getByRole('alert')).toContainText('The model is unavailable right now', { timeout: 25_000 });
  await expect(reply.getByRole('button', { name: 'Suggest again' })).toBeVisible();
});

test('summarizes and classifies a ticket', async ({ page, request }) => {
  const ticket = await createTicket(request);
  await openTicket(page, ticket.subject);

  await page.getByRole('button', { name: 'Summarize' }).click();
  await expect(page.getByText('The customer asks about their order and wants an update.')).toBeVisible();

  const classification = page.getByRole('region', { name: 'Classification' });
  await classification.getByRole('button', { name: 'Classify' }).click();
  await expect(classification.getByText('Other', { exact: true })).toBeVisible();
  await expect(classification.getByText('Normal', { exact: true })).toBeVisible();
  await expect(classification.getByRole('button', { name: 'Reclassify' })).toBeVisible();

  // The list shows the same badges
  const row = page.getByRole('link', { name: new RegExp(ticket.number) });
  await expect(row).toContainText('Other');
  await expect(row).toContainText('Normal');
});

test('the invalid-json scenario shows the needs-manual badge', async ({ page }) => {
  await openTicket(page, /T-1024/);
  const classification = page.getByRole('region', { name: 'Classification' });

  await classification.getByRole('button', { name: /^(Classify|Reclassify)$/ }).click();

  await expect(classification.getByText('Needs manual classification')).toBeVisible();
  await expect(page.getByRole('link', { name: /T-1024/ })).toContainText('Needs manual classification');
});

test('a ticket created through the API shows up in the list', async ({ page, request }) => {
  await page.goto('/tickets');
  const ticket = await createTicket(request, 'Created through the API');

  await page.reload();
  const link = page.getByRole('link', { name: ticket.subject });
  await expect(link).toBeVisible();
  await expect(link).toContainText(ticket.number);
  await link.click();
  await expect(page.getByRole('heading', { level: 1 }).filter({ hasText: ticket.number })).toContainText(ticket.subject);
});
