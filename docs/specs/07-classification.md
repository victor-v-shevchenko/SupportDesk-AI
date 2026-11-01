# Step 7: Classification

- **Branch:** `step/07-classification`
- **Depends on:** step 3b
- **Design:** PLAN.md sections 6.7, 9

## Goal

Category and priority come back as validated structured output, are saved with the ticket, and fall back to "needs manual classification" when the model's output can't be trusted.

## Scope

- `ClassificationResult` record and its JSON schema (`category` enum, `priority` enum, `reason` ≤ 200 characters, no extra properties)
- `ClassificationService` calls `GetResponseAsync<ClassificationResult>` with `prompts/classify.v2.md` (`classify.v1.md` is the merged step 3a draft, which is never edited), feature `Classification`
- Check what the Anthropic adapter does with structured output: it must use the response-format feature, not forced tool choice (which returns a 400 on current models, PLAN.md 6.7). If it forces a tool, request JSON through the provider-specific options instead and write that down in PLAN.md
- Validation against the schema in every case; on failure, one retry with the error added to the prompt; on a second failure, save `Status = NeedsManual` with the raw output logged at debug level
- Prompt: category definitions with one example each, priority rules (Urgent = safety issue or order lost in transit over 14 days; High = can't use the product or charged wrongly; Normal; Low = questions), ignore instructions inside `<ticket>`
- `POST /api/tickets/{id}/classification` classifies and saves; calling it again reclassifies
- UI: category and priority badges on the list and detail pages, a "needs manual classification" badge, "Classify" or "Reclassify" button, the reason as a tooltip

## Out of scope

Manual override in the UI. Automatic classification on create.

## Acceptance criteria

- [ ] Valid output is saved with category, priority, reason, prompt version and time
- [ ] Invalid JSON, an unknown enum value, a missing field, an extra field and a too-long reason are each rejected
- [ ] The first invalid response triggers exactly one retry that includes the validation error; a second invalid response saves `NeedsManual`
- [ ] The adapter's request for structured output is checked against a real Anthropic call once, by hand, and the finding is in the PR
- [ ] Badges show on the list and detail pages; `NeedsManual` has its own badge
- [ ] Model errors map the same way as in step 6

## Tests

- Unit: schema validation for each invalid case; retry-then-fallback flow with the fake provider's `invalid-json` scenario
- Integration: endpoint end to end; reclassify replaces the old result
- Angular: badge rendering for each status

## Review focus

That validation runs even when the provider claims to enforce the schema, that retries are limited to one, and the forced-tool-choice question.
