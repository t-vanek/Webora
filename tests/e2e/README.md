# Webora end-to-end tests

[Playwright](https://playwright.dev/) tests that drive the real Blazor app in a
browser. They cover authentication, the home dashboard, the parking reserve flow
(including the timezone round-trip regression), the admin pages and the
responsive layout.

## Prerequisites

- Node.js 18+
- The .NET SDK (the suite can start the app itself)
- The backing services running: `docker compose up -d postgres redis rabbitmq smtp4dev`

## Install

```bash
cd tests/e2e
npm install
npx playwright install chromium
```

## Run

```bash
# from tests/e2e
npm test               # headless
npm run test:headed    # headed
npm run test:ui        # interactive UI mode
npm run report         # open the last HTML report
```

By default the config starts the app with
`dotnet run --project src/Webora.Web/Webora.Web.csproj --urls http://localhost:5163`
and reuses an instance that is already listening. Point the tests at a different
instance with `BASE_URL`:

```bash
BASE_URL=https://staging.example.com npx playwright test
```

## Layout

- `tests/auth.setup.ts` — signs in once and stores the admin session for the
  authenticated specs.
- `tests/auth.spec.ts` — login (valid/invalid) and registration, run signed out.
- `tests/dashboard.spec.ts` — the home hero and quick-action tiles.
- `tests/parking.spec.ts` — leaderboard hero and a reserve round-trip.
- `tests/admin.spec.ts` — users/roles/spots lists and the settings tabs.
- `tests/responsive.spec.ts` — no horizontal overflow and the compact header at 390px.
- `tests/helpers.ts` — shared login/fill helpers and the admin credentials.

The credentials come from `IdentitySeed` in `src/Webora.Web/appsettings.json`
(`admin@webora.local` / `Admin123$`).
