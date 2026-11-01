# Step 8: Tool calling

- **Branch:** `step/08-tool-calling`
- **Depends on:** step 5
- **Design:** PLAN.md sections 4 (proposals and tasks), 7 (`tool_call` event), 10

## Goal

While writing a reply, the model can look up an order's status and propose a follow-up task. The user sees both in the reply panel, and a task is created only when the user confirms.

## Scope

**Order service**
- `IOrderStatusService` backed by `seed/orders.json`

**Tools**
- `lookup_order_status` and `propose_follow_up_task` per PLAN.md 10, registered as `AIFunction`s on the reply request only
- Argument validation before running; invalid arguments return an error result to the model
- Tool results are wrapped as data in the conversation (the model is told they are data)
- Check whether the Microsoft.Extensions.AI version in use has function-approval support. If it does and it fits, decide in the PR whether to use it instead of the proposal tool, and update PLAN.md 10

**Proposals and tasks**
- `FollowUpProposal` and `FollowUpTask` entities, migration `AddFollowUps`
- Proposal upsert on (ticket, reply run): a repeated or retried call in the same run updates it
- Expiry: proposals older than 24 hours are marked `Expired` by a background service every 10 minutes
- `GET /api/tickets/{id}/follow-ups`, `POST /api/follow-ups/{proposalId}/confirm` (`200` with the task, the same task on a second call, `409` if expired, `404` if unknown)

**Streaming and UI**
- `tool_call` events in the reply stream, sent after the tool runs
- Reply panel: an order status card for lookups; a proposal card with title, notes, due date and a Confirm button; a follow-ups list on the ticket page
- The prompt tells the model when to use each tool, and to propose a task only when the customer is waiting on something

## Changes made during the step

- `dueInDays` and `notes` are optional (PLAN.md 4 already has `DueDate?`); `title` is required. PLAN.md 10 has the full argument rules
- The `tool-call-proposal` scenario (T-1008) now looks up its order first: `KO-208815` isn't in `orders.json`, so the same scenario covers the unknown-order criterion
- Ticket and article text also have `tool_result` tags escaped (PLAN.md 6.3), since the prompt tells the model to trust tool results as data
- Function approval exists in Microsoft.Extensions.AI 10.11 but isn't used; the reasons are in PLAN.md 10

## Out of scope

Editing proposals. Other tools.

## Acceptance criteria

- [ ] For a ticket with an order number, the reply uses the order status (fake scenario `tool-call-order`; by hand with Ollama, screenshot in the PR)
- [ ] An unknown order number returns `{ "found": false }` and the reply says the order couldn't be found
- [ ] An invalid argument (bad order number pattern, too-long title, `dueInDays` out of range) returns an error result and doesn't throw
- [ ] Only the two registered tools can run; a call to any other name returns an error result
- [ ] `propose_follow_up_task` never creates a task; only the Confirm endpoint does
- [ ] Confirming twice creates one task; confirming an expired proposal returns `409`
- [ ] A retried or repeated proposal call in the same run leaves one proposal
- [ ] The tool loop stops at 3 iterations and the request cap still applies
- [ ] `tool_call` events arrive in order, and the panel shows the cards

## Tests

- Unit: argument validation; tool allow-list; proposal upsert; expiry
- Integration: reply with each fake tool scenario; confirm idempotency and `409`; follow-ups endpoint
- Angular: order card, proposal card, confirm flow

## Review focus

That no path creates a task without the Confirm endpoint, idempotency under retries, and how tool results are framed for the model.
