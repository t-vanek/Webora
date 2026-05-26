# Webora

Webora is a **parking reservation system for a shared/company car park**, built around an
**incentive system that maximises how well the lot is used**. Employees reserve spots for a time
window; the points system rewards behaviour that frees scarce spots — parking off-peak, releasing a
reservation you won't use, and sharing a reserved ("resident") spot — and penalises no-shows.

- **Default admin sign-in:** `admin@webora.local` / `Admin123$` (see `IdentitySeed` in
  `src/Webora.Web/appsettings.json`).
- **UI languages:** Czech (default) and English, negotiated from the culture cookie / browser.

## Parking & incentives

### Spots and reservations

- **Spots** have a code (`A-12`), a type (`Standard`, `Disabled`, `ElectricCharging`, `Visitor`,
  `Motorcycle`), an active flag and optional notes. Admins manage them at **`/admin/parking/spots`**.
- **Reservations** book a single spot for a time window. The lifecycle is a state machine:

  ```
  Reserved ──▶ CheckedIn ──▶ Completed        (the spot was used)
     │
     ├──▶ Released      (given up early — frees the spot for others)
     ├──▶ Cancelled     (called off)
     └──▶ NoShow        (not checked in by the grace deadline)
  ```

- Users reserve, check in ("Příjezd"), leave ("Odjezd"), release ("Uvolnit") or cancel at
  **`/parking`**, which also shows the points each action would earn.

### Points

Rewards are credited for **verified outcomes** (on completion / real use), never merely for booking:

| Reason | When | Notes |
| --- | --- | --- |
| **Off-peak bonus** | on completion | reservation started outside the peak window |
| **Release** | on early release | before the release cutoff; capped per user per day |
| **Shared-spot-taken** | on completion | took another resident's shared spot; scaled by commute distance |
| **Resident share** | on proactive release | scaled by how early + the resident's monthly allowance |
| **No-show penalty** | by the sweep | reservation never checked in past the grace period |
| **Share clawback** | by the sweep / reconciliation | a shared day that was wasted (guest no-show) or never booked |

Points feed a **leaderboard** (`/parking/leaderboard`) and **badges**: *Considerate Colleague*,
*Off-Peak Champion*, *Reliable Parker*, *Century Club*.

### Reserved spots for residents

A spot can be assigned a **resident owner** (e.g. a company-car holder) by an admin. The spot is then
**held for the resident each day until a configurable cutoff** (`ResidentHoldUntil` + the no-show
grace):

- The resident **confirms arrival** to keep it for the day, or **releases** it (for a single day or a
  date range) into the shared pool.
- If the resident neither confirms nor releases by the cutoff, the spot **auto-shares** for that day.
- **Conflict rule:** once a guest books a shared spot it is firm; a resident who turns up late
  competes for any free spot like everyone else (no bumping).
- A reminder is sent before the cutoff.

The **resident share reward** is graduated: `min(cap, hours_of_notice × rate) × (1 + allowance × pct/100)`.
The **monthly share allowance** the resident sets is both the reward multiplier **and a hard cap** on
how many rewarded shared days they get per calendar month. The reward is effectively contingent on
demand:

- guest used the spot → reward kept;
- guest booked but no-showed → partial clawback;
- nobody booked the released day → the reward is fully reversed by the daily reconciliation.

### Commute-distance factor

Taking a shared spot is rewarded more the farther the taker commutes (capped), so scarce spots flow
to those who need them most. Users enter a **home address** in their profile; it is **geocoded**
(Nominatim) and the **distance to the lot** is computed.

- Distance provider is pluggable: **Haversine** (straight-line, offline; default) or **OSRM** driving
  distance (`Distance:Provider = "Osrm"`), which **falls back to Haversine** if the routing service is
  unreachable.
- A self-reported address earns the distance reward only once **verified** — either by an admin (on
  the user-edit page) or **automatically** when within a configurable distance cap
  (`AutoVerifyHomeAddress`). The address can be removed by the user at any time.

### Anti-abuse hardening

The points system is hardened against farming:

1. Off-peak and distance rewards pay **on completion**, not at booking, so a reserve/release loop earns nothing.
2. Rewarded **releases are capped per day**.
3. The **monthly share allowance caps** rewarded shared days per month.
4. Released days **nobody booked are reconciled** and the reward reversed.
5. The distance reward requires a **verified address**.

### Background maintenance

A hosted service (`ParkingMaintenanceService`) runs on the configurable `SweepInterval` and, each
cycle: sends reservation reminders, sends resident hold reminders, resolves no-shows (with penalties
and notifications), and reconciles unused shared days. It can also be triggered manually from the
spots admin page.

### Roles & permissions

Fine-grained permissions gate the UI and services: `Parking.View`, `Parking.Reserve`,
`Parking.ViewLeaderboard`, `Parking.ManageSpots`, `Parking.ManageReservations`,
`Parking.ManageIncentives`. The seeded `Viewer`/`Editor` roles can view, reserve and see the
leaderboard; `Administrator` has everything.

## Configuration

Most parking behaviour is **stored in the database and edited live** at
**`/admin/parking/settings`** (`Parking.ManageIncentives`) — no redeploy needed. Tunables include:

- **Points:** release, off-peak bonus, no-show penalty.
- **Peak window:** start / end times.
- **Timing (minutes):** release cutoff, no-show grace, reminder lead, maintenance sweep interval.
- **Residents:** daily hold-until time, points-per-hour of notice, reward cap, max share allowance,
  multiplier % per allowed share, wasted-share clawback %.
- **Distance factor:** lot coordinates, base points, reference km, max multiplier.
- **Verification & limits:** auto-verify toggle + distance cap, max rewarded releases/day, max
  release range in days.

Infrastructure-level options live in `appsettings.json`:

```jsonc
"Geocoding": { "NominatimBaseUrl": "https://nominatim.openstreetmap.org", "UserAgent": "Webora/1.0 (parking)" },
"Distance":  { "Provider": "Haversine", "OsrmBaseUrl": "https://router.project-osrm.org" }
```

> Production note: outbound access to the geocoding (and, if used, routing) service must be allowed
> by the network policy, and Nominatim's usage policy (rate limit, identifying User-Agent) respected.
> Storing home addresses is personal data — obtain consent and set a retention policy.

## Engine

The engine is a .NET 10 solution organized along Clean Architecture lines:

| Project | Responsibility | Key dependencies |
| --- | --- | --- |
| `Webora.Domain` | Entities, value objects, domain rules. No framework dependencies. | — |
| `Webora.Application` | Use cases and Wolverine message handlers. | WolverineFx |
| `Webora.Infrastructure` | EF Core/Postgres persistence, Redis cache, ASP.NET Identity, OpenIddict stores, geocoding/distance. | EF Core, Npgsql, StackExchange.Redis, OpenIddict.EntityFrameworkCore |
| `Webora.Web` | Host: Blazor Web App (Auto), SignalR, Serilog, Wolverine + RabbitMQ, OpenIddict server, parking maintenance. | Serilog, WolverineFx.RabbitMQ, OpenIddict.AspNetCore |
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

### Run the app on the host

Start only the backing services and run the engine with the SDK — handy for debugging:

```bash
# 1. Backing services only
docker compose up -d postgres redis rabbitmq smtp4dev

# 2. Run the engine (Development auto-applies migrations and seeds the admin account)
dotnet run --project src/Webora.Web
```

Backing services are configured via `ConnectionStrings` in `src/Webora.Web/appsettings.json`
(`Postgres`, `Redis`, `RabbitMq`). RabbitMQ wiring activates only when its connection string is set;
clearing `ConnectionStrings:RabbitMq` (and `:Redis`) runs the app against Postgres alone.

Email is sent over SMTP via the `Smtp` configuration section. It defaults to the local smtp4dev
catcher (`localhost:2525`, no auth); inspect captured messages at http://localhost:5099. For
production set `Smtp:Authentication` to `Basic` or `OAuth2`.

### Database migrations

The `Development` environment applies migrations on startup. To manage them manually:

```bash
# apply the latest schema
dotnet ef database update --project src/Webora.Infrastructure --startup-project src/Webora.Web

# add a migration after a model change
dotnet ef migrations add <Name> --project src/Webora.Infrastructure --startup-project src/Webora.Web
```

The `dotnet-ef` tool is restored via `dotnet tool restore` (pinned in `dotnet-tools.json`).

## Tech notes

- **Blazor Web App** with both interactive render modes; parking pages render server-side
  (`InteractiveServer`), the notification bell runs on WebAssembly.
- **Localization:** UI strings live in `Webora.Web/Resources/SharedResource.*.resx`; server-side
  notification text in `Webora.Infrastructure/Resources/ParkingMessages.*.resx`.
- **Auth:** ASP.NET Core Identity (cookie sign-in) + OpenIddict server + permission-based RBAC.
