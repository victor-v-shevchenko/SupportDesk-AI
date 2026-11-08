# Reusable milestone specification template

Copy this template for each future vertical slice. Omit inapplicable risks with a reason rather than checking every box mechanically.

## Context
- **Goal / user outcome:**
- **Branch:**
- **Dependencies and affected components:**
- **Architecture decisions / links:**
- **Explicit out of scope:**

## Behavioral contract
- Inputs, outputs and validation:
- State owner and lifecycle:
- External provider/database assumptions:
- Trust boundaries and authorized effects:
- Observable user behavior:

## Invariants (must hold on every path)
1. 
2. 
3. 

Examples: no unconfirmed write; one terminal SSE event; cancellation never retries; telemetry never prevents persistence; stale responses never overwrite newer state.

## Risk / failure matrix

| Scenario | Expected result | Test layer | Test synchronization / proof |
| --- | --- | --- | --- |
| Success | | | |
| Invalid input / untrusted content | | | |
| Dependency failure / partial completion | | | |
| Cancellation / disconnect | | | |
| Timeout / retry | | | |
| Overlapping or out-of-order operations | | | |
| Recovery / restart / repeated operation | | | |
| Telemetry disabled or failing (if applicable) | | | |

## Acceptance criteria
- [ ] Each invariant is covered by a named test or justified manual verification.
- [ ] Tests prove their preconditions (e.g. retry backoff has actually started); no timing assumptions based only on sleep.
- [ ] At least one representative negative/mutation check shows the key test fails for broken behavior.
- [ ] Error codes and state transitions match neighboring features.
- [ ] Cross-component behavior is reviewed (caller, provider, persistence, UI, telemetry as applicable).
- [ ] Security/authorization and data handling are checked at each relevant boundary.
- [ ] Real-provider smoke/evaluation evidence is recorded when a provider contract or model behavior is changed; hosted costs require approval.
- [ ] Known limitations, risks and deviations are documented.

## Verification evidence
- Unit:
- Integration:
- Browser / E2E:
- Adversarial / fault injection:
- Real-provider evaluation (if relevant):
- Static analysis / CI:
- Negative test evidence:
- Remaining unverified assumptions:

## Independent review
### Before implementation
Design challenger: identify missing invariants, implicit assumptions, failure paths, provider constraints and potential overengineering.

### Before PR
Code reviewer: inspect diff plus affected execution paths; challenge tests for false positives, verify every acceptance criterion and flag any residual risk.

## PR handoff
- Spec changes and rationale:
- Implementation commits:
- Acceptance evidence:
- Bugs added to ai-log (root cause, detection stage, prevention):
- Known limitations / deferred items:
- Human approvals needed:
