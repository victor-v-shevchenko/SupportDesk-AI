# AI log

Bugs found in AI-written code, how they were found, and the test that now covers each one. Newest first.

Entry format (from CLAUDE.md):

```
## <date> — <short title>
- Step: <N>
- Found by: review subagent | test | browser check | user
- What was wrong: <one or two sentences>
- Fix: <one sentence, with the commit or PR>
- Test: <test name that now covers it>
```

## 2026-10-31 — Created ticket's timestamp differed from the stored one
- Step: 2
- Found by: test
- What was wrong: `POST /api/tickets` returned `createdAt` with .NET's 100 ns precision, but PostgreSQL keeps microseconds, so a later `GET` of the same ticket returned a slightly different time.
- Fix: `TicketRepository` truncates `CreatedAt` to microseconds before saving (step 2 PR).
- Test: `TicketEndpointTests.Create_returns_201_with_location_and_the_ticket_is_listed_first`
