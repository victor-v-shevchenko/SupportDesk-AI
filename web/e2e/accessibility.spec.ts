import AxeBuilder from '@axe-core/playwright';
import { expect, Page, test } from '@playwright/test';
import { createTicket, openTicket } from './support';

/**
 * What the page gives a keyboard and screen reader user:
 * - every link, button and form control in the ARIA snapshot (what a screen reader is given) has an accessible name;
 * - the Tab key reaches every visible, enabled control that can take focus (a native control, or an element with a tabindex);
 * - nothing that looks clickable (`cursor: pointer`) sits outside a focusable control, which would be a mouse-only action;
 * - axe finds no text below the AA contrast ratio.
 * Disabled controls are skipped on purpose: they are not meant to take focus.
 */
async function expectAccessible(page: Page): Promise<void> {
  const snapshot = await page.locator('body').ariaSnapshot();
  const unnamed = snapshot.split('\n').filter((line) => /^\s*- (button|link|textbox|searchbox|combobox|checkbox|radio|switch)(\s+\[[^\]]*\])*:?\s*$/.test(line));
  expect(unnamed, `controls without an accessible name in:\n${snapshot}`).toEqual([]);

  const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]';
  const mouseOnly = await page.evaluate((selector) => {
    return [...document.body.querySelectorAll<HTMLElement>('*')]
      .filter((el) => el.getClientRects().length > 0 && getComputedStyle(el).cursor === 'pointer' && !el.closest(selector) && !el.closest('label, [disabled]'))
      .map((el) => el.outerHTML.slice(0, 120));
  }, focusable);
  expect(mouseOnly, 'elements that look clickable but cannot take focus').toEqual([]);

  // Walk the page with Tab: each enabled, visible control must take focus
  const controls = await page.evaluate((selector) => {
    const visible = [...document.querySelectorAll<HTMLElement>(selector)].filter((el) => el.getClientRects().length > 0 && el.tabIndex >= 0);
    visible.forEach((el, i) => el.setAttribute('data-e2e-control', String(i)));
    return visible.length;
  }, focusable);
  expect(controls).toBeGreaterThan(0);

  const reached = new Set<string>();
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  for (let i = 0; i < controls * 2 + 10 && reached.size < controls; i++) {
    await page.keyboard.press('Tab');
    const id = await page.evaluate(() => document.activeElement?.getAttribute('data-e2e-control') ?? null);
    if (id !== null) {
      reached.add(id);
    }
  }
  const missed = await page.evaluate((seen) => {
    return [...document.querySelectorAll<HTMLElement>('[data-e2e-control]')]
      .filter((el) => !seen.includes(el.getAttribute('data-e2e-control') ?? ''))
      .map((el) => el.outerHTML.slice(0, 120));
  }, [...reached]);
  expect(missed, 'controls the Tab key did not reach').toEqual([]);

  const contrast = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
  expect(
    contrast.violations.flatMap((v) => v.nodes.map((n) => `${n.target.join(' ')}: ${n.failureSummary ?? ''}`)),
    'text below the AA contrast ratio',
  ).toEqual([]);
}

test('the ticket list is accessible by keyboard and every control is named', async ({ page }) => {
  await page.goto('/tickets');
  await expect(page.getByRole('link', { name: /T-1002/ })).toBeVisible();

  await expectAccessible(page);
});

test('the ticket page is accessible by keyboard and every control is named', async ({ page, request }) => {
  const ticket = await createTicket(request);
  await openTicket(page, ticket.subject);
  await page.getByRole('button', { name: 'Suggest reply' }).click();
  await expect(page.getByText('1,200 input + 48 output tokens')).toBeVisible();
  await page.getByRole('button', { name: 'Summarize' }).click();
  await expect(page.getByRole('button', { name: 'Regenerate' })).toBeVisible();

  await expectAccessible(page);
});

test('the proposal card is operable by keyboard', async ({ page, request }) => {
  // The reply is played from a mocked stream, so no other test's follow-ups on a seeded ticket change under this one
  const ticket = await createTicket(request);
  const proposal = { id: '0196f1a2-0000-7000-8000-0000000000aa', title: 'Open a carrier trace', notes: 'Still delayed.', dueDate: '2026-11-03', status: 'Pending' };
  const task = { id: 1, proposalId: proposal.id, title: proposal.title, notes: proposal.notes, dueDate: proposal.dueDate, createdAt: '2026-11-01T12:00:00Z' };
  const events: [string, unknown][] = [
    ['token', { text: 'An agent will follow up.' }],
    ['tool_call', { name: 'propose_follow_up_task', proposal }],
    ['citations', { citations: [], removed: [] }],
    ['usage', { inputTokens: 10, outputTokens: 5, costUsd: 0, estimated: false }],
    ['done', { replyRunId: '00000000-0000-0000-0000-000000000001' }],
  ];
  let confirmed = false;
  await page.route(`/api/tickets/${ticket.id}/reply`, (route) =>
    route.fulfill({ contentType: 'text/event-stream', body: events.map(([event, data]) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`).join('') }),
  );
  await page.route(`/api/tickets/${ticket.id}/follow-ups`, (route) =>
    route.fulfill({ json: confirmed ? { proposals: [], tasks: [task] } : { proposals: [proposal], tasks: [] } }),
  );
  await page.route(`/api/follow-ups/${proposal.id}/confirm`, (route) => {
    confirmed = true;
    return route.fulfill({ json: task });
  });
  await openTicket(page, ticket.subject);
  await page.getByRole('button', { name: 'Suggest reply' }).click();
  const confirm = page.getByRole('region', { name: 'Suggested reply' }).getByRole('button', { name: 'Confirm' });
  await expect(confirm).toBeVisible();
  await expectAccessible(page);

  await confirm.focus();
  await expect(confirm).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('status').filter({ hasText: 'Task created.' })).toBeVisible();
});

test('the knowledge base is accessible by keyboard and every control is named', async ({ page }) => {
  await page.goto('/articles');
  await expect(page.getByRole('link', { name: 'Refunds' }).first()).toBeVisible();
  await page.getByLabel('Question').fill('refund');

  await expectAccessible(page);
});

test('an article page is accessible by keyboard and every control is named', async ({ page }) => {
  await page.goto('/articles');
  await page.getByRole('list').getByRole('link').first().click();
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  await expectAccessible(page);
});

test('the focused control has a visible outline', async ({ page }) => {
  await page.goto('/tickets');
  await expect(page.getByRole('link', { name: /T-1002/ })).toBeVisible();

  await page.keyboard.press('Tab');
  const outline = await page.evaluate(() => {
    const style = getComputedStyle(document.activeElement as Element);
    return { width: parseFloat(style.outlineWidth), style: style.outlineStyle };
  });
  expect(outline.style).not.toBe('none');
  expect(outline.width).toBeGreaterThanOrEqual(2);
});

for (const scheme of ['light', 'dark'] as const) {
  test(`text and form controls have enough contrast in the ${scheme} theme`, async ({ page, request }) => {
    await page.emulateMedia({ colorScheme: scheme });
    const ticket = await createTicket(request);
    await openTicket(page, ticket.subject);
    await page.getByRole('button', { name: 'Suggest reply' }).click();
    await expect(page.getByText('1,200 input + 48 output tokens')).toBeVisible();
    await page.getByRole('button', { name: 'Classify' }).click();
    await expect(page.getByRole('button', { name: 'Reclassify' })).toBeVisible();

    const contrast = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
    expect(contrast.violations.flatMap((v) => v.nodes.map((n) => n.target.join(' ')))).toEqual([]);

    // WCAG 1.4.11: the border of a form control against what is behind it is at least 3:1 (axe does not check this)
    const ratio = await page.evaluate(() => {
      const parse = (color: string): number[] => (color.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
      const luminance = ([r, g, b]: number[]): number => {
        const [lr, lg, lb] = [r, g, b].map((c) => {
          const v = c / 255;
          return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
        });
        return 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
      };
      const button = document.querySelector<HTMLElement>('button.suggest')!; // "Suggest again"
      const border = luminance(parse(getComputedStyle(button).borderTopColor));
      const page = luminance(parse(getComputedStyle(document.body).backgroundColor));
      const [light, dark] = border > page ? [border, page] : [page, border];
      return (light + 0.05) / (dark + 0.05);
    });
    expect(ratio).toBeGreaterThanOrEqual(3);
  });
}
