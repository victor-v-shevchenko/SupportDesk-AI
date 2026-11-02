import { APIRequestContext, expect, Page } from '@playwright/test';

/** A ticket made through the API for one test, so no test depends on what another one changed. */
export interface CreatedTicket {
  id: number;
  number: string;
  subject: string;
}

let counter = 0;

/** A subject no other test or earlier run uses, so the test can find its own ticket in the list. */
export function uniqueSubject(label: string): string {
  counter += 1;
  return `${label} ${Date.now().toString(36)}-${counter}-${Math.random().toString(36).slice(2, 6)}`;
}

export async function createTicket(request: APIRequestContext, label = 'E2E ticket'): Promise<CreatedTicket> {
  const subject = uniqueSubject(label);
  const response = await request.post('/api/tickets', {
    data: { subject, body: 'Where is my parcel? It was due last week.', customerName: 'Sam Rivera', orderNumber: null },
  });
  expect(response.status()).toBe(201);
  const created = (await response.json()) as CreatedTicket;
  return { id: created.id, number: created.number, subject };
}

/**
 * Opens a ticket from the list the way a person does. The fake scenarios belong to the seeded tickets' numbers
 * (PLAN.md 6.8), so those are opened by number; a ticket made by a test has a subject of its own.
 */
export async function openTicket(page: Page, name: string | RegExp): Promise<void> {
  await page.goto('/tickets');
  await page.getByRole('link', { name }).click();
  await expect(page.getByRole('heading', { level: 1 }).filter({ hasText: /T-\d+/ })).toBeVisible();
}

/** The numbers in the cost readout: "1,767 tokens (1,400 input + 70 output)". */
export async function readCost(page: Page): Promise<{ input: number; output: number; calls: number; costUsd: number }> {
  const text = (await page.locator('.cost .totals').innerText()).replace(/\s+/g, ' ');
  const match = /\(([\d,]+) input \+ ([\d,]+) output\).*\$([\d,.]+).* (\d+) calls?/.exec(text);
  expect(match, `cost readout: ${text}`).not.toBeNull();
  const [, input, output, cost, calls] = match!;
  return { input: Number(input.replaceAll(',', '')), output: Number(output.replaceAll(',', '')), calls: Number(calls), costUsd: Number(cost.replaceAll(',', '')) };
}
