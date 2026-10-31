# Step 2: Tickets and knowledge base

- **Branch:** `step/02-tickets-kb`
- **Depends on:** step 1
- **Design:** PLAN.md sections 4 (data model), 5 (API)

## Goal

Tickets and help articles are stored, seeded and shown in the UI. Seed data includes everything later steps need: order numbers, articles with headings for chunking, and tickets that exercise each category.

## Scope

**Entities and migration**
- `Ticket` with the owned `TicketSummary` and `TicketClassification` (all fields nullable or `NotRun` for now), `Article`
- `Category`, `Priority`, `TicketStatus`, `ClassificationStatus` enums stored as strings
- Migration `AddTicketsAndArticles`

**Seed data** (fictional store: Kestrel Outfitters)
- `seed/tickets.json`: about 25 tickets spread over all six categories and four priorities. At least 10 have an `orderNumber` (`KO-` + 6 digits); one has an order number that isn't in `orders.json`; two contain an attempted prompt injection ("ignore previous instructions…"), to be used in steps 9 and 10
- `seed/articles/*.md`: 12 articles with front matter (`slug`, `title`) and `##` sections, covering shipping times, returns, refunds, warranty, account and password, order changes, sizing, damaged items, international shipping, payment methods, gift cards, contacting support
- `seed/orders.json`: 10 orders with status (Processing, Shipped, Delivered, Delayed, Cancelled), carrier and last update
- Seeding runs at startup in Development when the tables are empty, and is idempotent (matched on ticket number and article slug)

**Endpoints**
- `GET /api/tickets` (newest first, optional `status` filter), `GET /api/tickets/{id}`, `POST /api/tickets`
- `GET /api/articles`, `GET /api/articles/{slug}`
- `POST /api/tickets` validates: subject 1–200 characters, body 1–5,000 characters, customer name required, order number optional but must match `KO-\d{6}`. It returns `201` with the location. Invalid input returns `400` problem details listing each field

**Angular**
- Ticket list page (number, subject, status, created) and ticket detail page (all fields, empty slots for summary, classification and reply)
- An API service in `web/src/app/api/` with typed models

## Clarified during implementation

These fill gaps in the scope above; the details are in PLAN.md 4 and 5.

- **"When the tables are empty" and "idempotent":** seeding runs on every Development start and adds only what's missing, which covers both: an empty database gets everything, a restart adds nothing. Existing tickets are never overwritten. Articles whose file changed are updated, so edited seed articles reach the database (step 4 needs this to re-embed them)
- **Category and priority in `tickets.json`:** the `Ticket` entity has no category or priority of its own, only the classification (step 7). So each seed ticket has `category` and `priority` fields with the labels it was written for, used to check coverage. They aren't stored, and classification starts as `NotRun`
- **Orders:** orders aren't stored in the database (PLAN.md 4). The first criterion's "seeded with every … order file" is covered by tests that check `orders.json` against the tickets
- **Customer name length:** at most 200 characters (the spec only said "required"), so the column has a limit
- **Ticket numbers** for created tickets come from a PostgreSQL sequence that continues after the highest seeded number
- **`?status=`** accepts a status name in any case; anything else returns `400` problem details naming `status`
- **No create form or articles page in the UI:** the Angular scope lists only the ticket list and detail pages

## Out of scope

Order service (step 8), chunks and embeddings (step 4), any LLM call.

## Acceptance criteria

- [ ] A fresh database is seeded with every ticket, article and order file; restarting doesn't duplicate anything
- [ ] Every category and every priority appears in the seed tickets at least once
- [ ] At least 10 tickets have order numbers, one points to a missing order, and two contain injection attempts
- [ ] Every article has front matter and at least two `##` sections
- [ ] The list and detail endpoints return the documented shapes; an unknown ID or slug returns `404` problem details
- [ ] `POST /api/tickets` creates a ticket and returns `201`; each invalid field gives a `400` that names it
- [ ] The ticket list and detail pages show seeded data, with loading, empty and error states
- [ ] Entities are never returned directly from endpoints

## Tests

- Integration: seeding is idempotent; list order and filter; detail; `404`; create success and each validation failure
- Unit: order-number validation
- Angular: list and detail components render data and the error state

## Review focus

Validation completeness, seed idempotency, that the seed data really supports the later steps (orders, injection tickets, headings).
