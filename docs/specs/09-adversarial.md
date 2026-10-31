# Step 9: Adversarial tests

- **Branch:** `step/09-adversarial`
- **Depends on:** step 8
- **Design:** PLAN.md sections 6.3, 6.5, 7, 10

## Goal

Try to break the defenses built in steps 3–8, deterministically, and fix what breaks. This step adds tests first; code changes are only fixes for what the tests find.

## Scope

**Prompt injection** (with the fake provider, checking what reaches the model and what the system does with the model's output)
- Ticket text containing `</ticket>`, fake system messages, "ignore previous instructions", and instructions to call tools
- Article text and tool results containing instructions
- Model output that calls `propose_follow_up_task` many times, with injected text in the arguments, or calls an unregistered tool
- Model output citing markers that don't exist or citing huge numbers

**Provider faults**
- 429 with and without `Retry-After`; 500, 502, 503; connection reset; timeout before the first item
- An error inside the stream after text; an error after a tool call
- A stall longer than the gap timeout; a stream that never sends usage
- A refusal

**Client and transport**
- Client disconnects mid-stream; client aborts and immediately starts a new reply
- Two replies for the same ticket at once from two tabs
- Very long ticket body (at the 5,000-character limit) with many retrieved chunks

**Fixes**
- Each failure found gets a failing test, a fix, and an entry in `docs/ai-log.md`

## Out of scope

Real-model injection testing (step 10).

## Acceptance criteria

- [ ] Every case above has a test, grouped by category, and every test passes
- [ ] No injected input causes a task to be created, a tool outside the allow-list to run, or user text to reach the system prompt
- [ ] Repeated proposal calls in one run leave one proposal
- [ ] Every fault ends the stream with exactly one `error` or `done`, and every call leaves exactly one `ModelCalls` row per inner call
- [ ] A client disconnect cancels the provider call within one second (fake clock)
- [ ] Two concurrent replies for one ticket both complete, each with its own reply run and proposals
- [ ] Each bug found is in `docs/ai-log.md` with its test

## Tests

This step is tests. Keep them deterministic: fake provider, fake clock, no real network.

## Review focus

Whether the cases are realistic and complete, and whether any test passes for the wrong reason (for example, asserting only that no exception was thrown).
