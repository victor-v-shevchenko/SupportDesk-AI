# Step 11: UI polish and end-to-end tests

- **Branch:** `step/11-ui-e2e`
- **Depends on:** step 8 (can run in parallel with step 10)
- **Design:** PLAN.md sections 7, 11

## Goal

The UI is clean and consistent across the pages built in steps 2–8, and Playwright end-to-end tests cover the main flows in CI.

## Scope

**UI**
- One consistent layout: ticket list on the left, ticket detail on the right on wide screens; stacked on narrow screens
- Consistent loading, empty and error states; error messages for each API error code
- Keyboard access for every action; visible focus; labels on icon buttons; color contrast at WCAG AA
- Cost readout on the ticket page from `GET /api/model-calls?ticketId=` (total tokens and cost for this ticket)
- Light and dark theme following the system setting

**End-to-end tests** (`web/e2e/`, `@playwright/test`)
- Run against the API with `AI__Chat__Provider=Fake` and `AI__Embeddings__Provider=Fake`, a fresh database, and the Angular build
- Flows:
  1. Open the list, open a ticket, see its details
  2. Suggest a reply: text streams in, citations link to articles, cost line shows
  3. Stop a reply mid-stream; start a new one
  4. Reply with an order lookup shows the order card
  5. Reply with a proposal; confirm it; the task appears; confirming again doesn't add one
  6. Model error before the first item shows the "model unavailable" message
  7. Summarize and classify; badges and summary appear; `NeedsManual` shows its badge for the `invalid-json` scenario
  8. Create a ticket through the API, then find it in the list
- Screenshots on failure, kept as CI artifacts

**CI**
- A second CI job: start PostgreSQL (service container) with its password from the `CI_POSTGRES_PASSWORD` GitHub secret, run the API with the fake provider, build and serve the Angular app, run Playwright with Chromium
  - *Why:* CLAUDE.md keeps every password out of the workflow file, including throwaway ones, so the rule has no exceptions and gitleaks has nothing to allow
- The job is required for merging

## Done by the user

1. Before the end-to-end job first runs: create the `CI_POSTGRES_PASSWORD` repository secret (any random value)
2. After the job has run once on the branch: add it to the required checks for `main` (or approve Claude Code doing it with `gh api`)

## Out of scope

New features. Visual regression testing.

## Acceptance criteria

- [ ] All eight flows pass locally in the dev container and in CI
- [ ] Tests wait on visible state (text, roles), never on fixed sleeps
- [ ] The end-to-end job is required in branch protection
- [ ] Every interactive element is reachable by keyboard and has an accessible name (checked with Playwright's accessibility snapshot on each page)
- [ ] Layout works at 375 px and 1440 px wide (screenshots in the PR)
- [ ] The cost readout matches the sum of the ticket's `ModelCalls` rows

## Tests

The end-to-end suite, plus component tests for any new UI pieces.

## Review focus

Flaky patterns (timing, shared state between tests), accessibility, and whether the tests check what the user sees rather than implementation details.
