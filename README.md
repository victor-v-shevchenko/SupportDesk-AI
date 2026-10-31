# SupportDesk AI

A support-ticket assistant with production-style LLM features: ASP.NET Core (.NET 10) Minimal API, Angular, PostgreSQL + pgvector and Microsoft.Extensions.AI.

> Work in progress. The design is in [PLAN.md](PLAN.md) and the steps are in [docs/specs/](docs/specs/).

## Quick start

1. Copy `.env.example` to `.env` and fill it in (at least `POSTGRES_PASSWORD`)
2. Either:
   - **Docker only:** run `docker compose up --build`, then open http://localhost:4200 (API health: http://localhost:5080/api/health)
   - **Dev container:** open the folder in VS Code and run "Reopen in Container". Then `dotnet run --project src/SupportDesk.Api` and `npm --prefix web start`

Run one way at a time: both use ports 4200, 5080 and 5432 (`docker compose stop api web` before running the apps in the dev container).

The commands for building, testing and migrations are in [CLAUDE.md](CLAUDE.md#commands).
