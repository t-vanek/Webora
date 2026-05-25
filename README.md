# Webora
Webora is a lightweight web presentation system for creating simple websites for individuals, small businesses, organizations, and portfolios.

## Engine

The engine is a .NET 10 solution organized along Clean Architecture lines:

| Project | Responsibility | Key dependencies |
| --- | --- | --- |
| `Webora.Domain` | Entities, value objects, domain rules. No framework dependencies. | — |
| `Webora.Application` | Use cases and Wolverine message handlers. | WolverineFx |
| `Webora.Infrastructure` | EF Core/Postgres persistence, Redis cache, ASP.NET Identity, OpenIddict stores. | EF Core, Npgsql, StackExchange.Redis, OpenIddict.EntityFrameworkCore |
| `Webora.Web` | Host: Blazor Web App (Auto), SignalR, Serilog, Wolverine + RabbitMQ, OpenIddict server. | Serilog, WolverineFx.RabbitMQ, OpenIddict.AspNetCore |
| `Webora.Web.Client` | Blazor WebAssembly client components. | — |

The dependency flow is `Domain ← Application ← Infrastructure ← Web`.

## Getting started

Prerequisites: .NET 10 SDK and Docker.

```bash
# 1. Start Postgres, Redis, RabbitMQ and smtp4dev
docker compose up -d

# 2. Apply the database schema (Identity + OpenIddict tables)
dotnet dotnet-ef database update \
  --project src/Webora.Infrastructure \
  --startup-project src/Webora.Infrastructure

# 3. Run the engine
dotnet run --project src/Webora.Web
```

Backing services are configured via `ConnectionStrings` in `src/Webora.Web/appsettings.json`
(`Postgres`, `Redis`, `RabbitMq`). RabbitMQ wiring activates only when its connection string is set.

Email is sent over SMTP via the `Smtp` configuration section. It defaults to the local smtp4dev
catcher (`localhost:2525`, no auth); inspect captured messages at http://localhost:5099. For
production set `Smtp:Authentication` to `Basic` or `OAuth2` — `OAuth2` uses the generic
client-credentials grant configured under `Smtp:OAuth2` (token endpoint, client id/secret, scope).
