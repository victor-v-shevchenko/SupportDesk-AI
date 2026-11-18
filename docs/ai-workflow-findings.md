# Lessons learned: AI-assisted engineering workflow

Derived from [the defect log](ai-log.md), [the project plan](../PLAN.md), [agent instructions](../CLAUDE.md), and milestone specifications. This is a retrospective, not proof of the frequency of bugs in all AI-written code.

## Findings and preventive controls

| Observed failure | Likely process gap | Prevention | Verification |
| --- | --- | --- | --- |
| Concurrent migration/seed startup | No explicit concurrent-start invariant | Document ownership, idempotency and multi-instance startup | Start two instances against an empty database |
| Faulted lazy order cache | Recovery not specified | Define cache fault eviction and retry semantics | Fail first load, recover on second |
| Timeout/cancellation bugs | Async lifecycle not modeled end-to-end | Define cancellation source, ownership, propagation and stream phase | Deterministic first-item, gap, backoff and disconnect tests |
| Retry test passed before entering backoff | Test did not prove precondition | Explicit synchronization barrier for state under test | Inject bug and confirm test fails |
| E2E Tab walk skipped excluded controls | Test filtered out failures | Assert all visible controls first, then walk focus order | Temporarily set tabindex=-1 and verify failure |
| Injection through ticket/source/tool text | Threat analysis limited to a feature | Enumerate all untrusted entry points and output sinks | Test each source-to-sink path |
| Fabricated refund promise survived citation parsing | Syntactic citation validation mistaken for factual grounding | Add claim-level grounding checks, refusal cases and real-model eval | Unsupported policy assertion must fail even with no citation marker |
| Tool fan-out exceeded iteration limit | Bound was applied to rounds, not total work | Cap total calls, concurrency, per-call cost and effects | One response with many calls cannot exceed cap |
| Classification and retrieval errors differed from reply errors | Error policy reviewed locally | Maintain cross-feature error taxonomy | Provider-failure contract tests per entry point |
| UI list badges and cost readout became stale | State ownership and overlap not specified | Define invalidation, request identity and monotonic progress | Out-of-order and overlapping response tests |
| Telemetry broke SaveChanges / metrics listener | Cross-cutting concern treated as harmless | Observability must not alter business results | Telemetry on/off, throwing listener/exporter and privacy tests |
| Token estimate undercounted provider | Provider behavior assumed from heuristic | Verify external contracts empirically | Provider count comparison with safety margin |
| Ollama exhausted output on thinking | Model capability/config differences | Model-specific smoke tests and output budgets | Real-provider finish-reason tests |

## What to change in the next project

1. **Before coding:** write the architecture, trust boundaries, state ownership and five highest-risk failure modes. Review the design independently.
2. **For each vertical slice:** define invariants and a failure matrix before implementation. Keep slices small enough for end-to-end evidence.
3. **During implementation:** test success, failure, cancellation, retry, concurrency, and recovery where relevant. Use barriers/fake clocks rather than sleeps.
4. **Before merging:** independent reviewer examines the diff **and affected callers, providers, data stores, UI consumers and test assertions**. Demonstrate a test would fail on a representative broken implementation.
5. **At architecture boundaries:** review the entire feature flow, not just the latest diff. Suggested boundaries: after infrastructure/persistence, after AI pipeline, after tool calling, and before release.
6. **For LLM features:** run small opt-in real-model evaluations when the feature first appears; keep deterministic CI tests. Measure unsupported factual claims independently of citation syntax.
7. **For telemetry:** design privacy and noninterference at the beginning; add exporters later if desired.
8. **For the defect log:** tag every defect by root cause, missed invariant, detection stage and reusable prevention rule. Revisit the checklist after each milestone.

## Review roles

- **Implementer:** implements a bounded slice and supplies tests and evidence.
- **Design challenger:** read-only, independent context/model; critiques missing invariants, trust boundaries and operational failure modes *before* implementation.
- **Code reviewer:** read-only, independent context/model; traces changed behavior across the whole execution path and challenges tests.
- **Human owner:** approves scope changes, real-provider costs, risk exceptions and merge. An agent's confidence is not verification.

Small documentation-only changes may use a lightweight review; do not require costly provider evaluations or full end-to-end runs without relevant changes.

## Prioritized changes to SupportDesk-AI

- **P0:** Extend evaluation to flag unsupported policy/refund promises even when citation markers are absent; document the known qwen2.5:7b failure as unresolved until demonstrated otherwise.
- **P0:** Require test precondition evidence and cross-feature boundary review in the reviewer instructions.
- **P1:** Move injection, cancellation, concurrency and recovery test cases into feature-specific acceptance criteria rather than deferring them to step 9.
- **P1:** Add a contract matrix for provider failures, cancellation, stream completion, data persistence and UI invalidation.
- **P1:** Add telemetry noninterference/privacy regression requirements to every observability change.
- **P2:** Add lightweight architecture checkpoints after persistence, AI pipeline, tools and before release.

## Caveats

A documented fix does not establish that all similar paths are safe. The log is selected evidence, not a denominator for defect rates. Real-model behavior is model-, prompt-, and configuration-dependent. A prompt rule is not a deterministic safety boundary.
