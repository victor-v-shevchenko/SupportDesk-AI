# Step 12: Final review and README

- **Branch:** `step/12-readme`
- **Depends on:** steps 10 and 11
- **Design:** the whole of PLAN.md

## Goal

A whole-project review catches what per-step reviews can't see, and the README presents the project to someone opening the repository for the first time.

## Scope

**Whole-project review**
- Run the `reviewer` subagent over `git diff <first commit>...HEAD` (or, if that's too large, over each folder in turn), with this prompt focus instead of a step spec:
  - Does the code match PLAN.md? List every difference
  - Consistency across steps: error handling, naming, logging, options, test style
  - Dead code, unused config keys, leftover TODOs
  - Anything a per-step review would miss because it spans steps (for example, a middleware assumption that a later feature breaks)
- Fix findings on this branch, or open `fix/` branches for larger ones; record bugs in `docs/ai-log.md`
- Update PLAN.md wherever the code is right and the plan is out of date

**README**
- Layout: badges, screenshot, one-paragraph description, quick start, design notes, tests table, code quality, AI tools, evaluation results, bugs found and fixed, possible improvements
- **Badges:** CI status, SonarQube Cloud quality gate, coverage, each linking to its page. The Sonar badges come from `https://sonarcloud.io/api/project_badges/measure?project=victor-v-shevchenko_SupportDesk-AI&metric=alert_status` (and `metric=coverage`), linking to the project dashboard
- **Code quality:** the quality gate used, current coverage and duplication from SonarQube Cloud, the static analysis in the build (.NET analyzers, ESLint, gitleaks), and the rules turned off with their reasons (summarized from PLAN.md 11)
- **Quick start:** dev container path and plain Docker Compose path; how to switch providers; how to run with the fake model and no API key
- **Design notes:** the pipeline diagram and middleware order, why retries live in middleware, streaming and citations, the proposal-and-confirm pattern, the token budget. Link to PLAN.md for detail
- **Tests table:** suite, what it covers, how to run, count of tests
- **AI tools:** how Claude Code built it step by step, the review subagent, and a table of each step with the model that implemented it and the model that reviewed it
- **Evaluation results:** the latest report's numbers for each model, with cost, linking to `docs/eval/`
- **Bugs found and fixed:** a summary of `docs/ai-log.md`, grouped by who found them (review subagent, tests, browser checks, user)
- **Possible improvements:** honest list (for example LLM-as-judge grading, auth, real email intake, prompt caching)
- Screenshot of the ticket page with a streamed reply, citations and a proposal card, taken with the Playwright MCP server, in `docs/images/`

**Release**
- Tag `v1.0` after merge

## Out of scope

New features.

## Acceptance criteria

- [ ] The whole-project review ran, and each finding is fixed, moved to a `fix/` branch, or answered in the PR
- [ ] PLAN.md matches the code
- [ ] Following the README's quick start on a fresh clone works with no steps missing, both with the dev container and with plain Docker Compose using the fake model
- [ ] Every number in the README (tests, evaluation results, cost, coverage) comes from a real run and says when it was taken
- [ ] The badges show the live status and link to the CI run and the SonarQube Cloud project
- [ ] The SonarQube Cloud quality gate passes on `main`
- [ ] The implemented-by and reviewed-by table covers every step
- [ ] The CLAUDE.md commands table still works
- [ ] `v1.0` is tagged

## Review focus

Accuracy of every claim in the README, and whether a newcomer could run the project from it alone.
