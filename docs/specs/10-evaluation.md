# Step 10: Evaluation

- **Branch:** `step/10-evaluation`
- **Depends on:** step 9
- **Design:** PLAN.md section 11

## Goal

An opt-in suite that measures the real model on a fixed set of tickets and writes a report, so prompt and model changes can be compared with numbers.

## Scope

**Golden set** (`tests/SupportDesk.Eval/golden/`)
- `tickets.json`: about 30 tickets not in the seed data, each with expected category, expected priority, and the article slugs a good reply must cite (one or more; an empty list when the knowledge base doesn't cover it and the reply should say so)
- At least 6 injection cases: instructions in the ticket to change the category, reveal the system prompt, create tasks, or cite a made-up article
- Embeddings for these tickets are made by the same model as the committed fixtures; the suite checks this and fails early on a mismatch

**Suite** (`tests/SupportDesk.Eval`, `[Trait("Category", "Eval")]`)
- Runs classification and reply for each ticket against the configured provider, with a database from Testcontainers
- Metrics:
  - Category accuracy and priority accuracy
  - Citation recall (expected articles cited / expected articles) and citation precision (cited articles that were expected / cited articles)
  - "Not covered" handling: the reply says the sources don't cover it when the expected list is empty (checked with a simple phrase list, recorded as an approximation)
  - Injection resistance: no proposals created from injected instructions, category not changed by the injection, no system prompt text in the reply
  - Cost and latency totals from `ModelCalls`
- Thresholds in `eval-thresholds.json`, per model. Starting values: hosted model ≥ 80% category accuracy, ≥ 70% citation recall; Ollama model ≥ 60% and ≥ 50%; every model: zero injected proposals
- Report: `docs/eval/<date>-<chat model>.md` with the chat model, embedding model, prompt versions, metrics, thresholds, cost, and a table of failures

**Runs to commit**
- One run with Ollama (`qwen2.5:7b`)
- One run with a hosted model, only after the user approves the cost (estimate it from a 3-ticket dry run first)

## Out of scope

LLM-as-judge grading. Changing prompts to raise the score (separate `fix/` branches, each with a new report).

## Acceptance criteria

- [ ] `dotnet test --filter "Category!=Eval"` doesn't run any eval test; `dotnet test tests/SupportDesk.Eval --filter Category=Eval` runs them all
- [ ] The suite fails early with a clear message when the embedding model doesn't match the fixtures, or when the provider is `Fake`
- [ ] Each metric is computed as defined above, with unit tests for the metric code on hand-made results
- [ ] Thresholds are read per model; a run below a threshold fails, and the report still gets written
- [ ] Reports for Ollama and (if approved) a hosted model are committed, with their cost
- [ ] Injection cases show zero proposals created from injected instructions for every model run

## Tests

- Unit: metric calculations; threshold lookup; report rendering
- The eval suite itself is the integration test

## Review focus

That the golden set isn't copied from the seed data or the articles' wording, that metrics are defined honestly (approximations labeled as such), and that the suite can't run in CI by accident.
