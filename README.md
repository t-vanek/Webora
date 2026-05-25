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

Prerequisites: Docker. (The .NET 10 SDK is only needed for the host-side workflow below.)

### Run everything in Docker

```bash
docker compose up --build
```

This builds the app image and starts it next to Postgres, Redis, RabbitMQ and smtp4dev. The
container runs in the `Development` environment, so on startup it applies the EF migrations and
seeds the admin account. Once it's up:

- App: http://localhost:8080
- Captured email (smtp4dev): http://localhost:5099
- RabbitMQ management: http://localhost:15672 (`guest` / `guest`)

Default admin sign-in is `admin@webora.local` / `Admin123$` (see the `IdentitySeed` section in
`src/Webora.Web/appsettings.json`).

### Run the app on the host

Start only the backing services and run the engine with the SDK — handy for debugging:

```bash
# 1. Backing services only
docker compose up -d postgres redis rabbitmq smtp4dev

# 2. Run the engine (Development auto-applies migrations and seeds the admin account)
dotnet run --project src/Webora.Web
```

To apply the database schema manually (e.g. outside the Development environment):

```bash
dotnet dotnet-ef database update \
  --project src/Webora.Infrastructure \
  --startup-project src/Webora.Infrastructure
```

Backing services are configured via `ConnectionStrings` in `src/Webora.Web/appsettings.json`
(`Postgres`, `Redis`, `RabbitMq`). RabbitMQ wiring activates only when its connection string is set.

Email is sent over SMTP via the `Smtp` configuration section. It defaults to the local smtp4dev
catcher (`localhost:2525`, no auth); inspect captured messages at http://localhost:5099. For
production set `Smtp:Authentication` to `Basic` or `OAuth2` — `OAuth2` uses the generic
client-credentials grant configured under `Smtp:OAuth2` (token endpoint, client id/secret, scope).
