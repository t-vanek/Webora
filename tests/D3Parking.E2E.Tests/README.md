# D3Parking.E2E.Tests

[Playwright for .NET](https://playwright.dev/dotnet/) end-to-end tests
(`Microsoft.Playwright.NUnit`) that drive the real Blazor app in a browser. They
cover authentication, the home dashboard, the parking flow (including the
timezone round-trip regression), the admin pages and detail forms, the account
pages and the responsive layout.

## Prerequisites

- .NET SDK 10
- A reachable Microsoft SQL Server — by default SQL Server LocalDB
  (`(localdb)\MSSQLLocalDB`), see `ConnectionStrings:SqlServer` in
  `src/D3Parking.Web/appsettings.json`. Development applies the migrations on start.
- The Playwright browser. After the first build, install it once:

  ```bash
  pwsh tests/D3Parking.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
  ```

  The `WebAppFixture` also runs `playwright install chromium` on start, so a
  plain `dotnet test` will fetch it if missing.

## Run

```bash
dotnet test tests/D3Parking.E2E.Tests/D3Parking.E2E.Tests.csproj
```

`WebAppFixture` starts the app with
`dotnet run --project src/D3Parking.Web/D3Parking.Web.csproj --urls http://localhost:5163`
if nothing is already listening, signs in once as the seeded admin and stores
the session for the authenticated fixtures. Point the suite at another instance
(and skip the self-start) with `BASE_URL`:

```bash
BASE_URL=https://staging.example.com dotnet test
```

Headed / debugging:

```bash
HEADED=1 PWDEBUG=1 dotnet test
```

## Layout

- `WebAppFixture.cs` — assembly setup: ensures the app is up and saves the admin session.
- `Pages.cs` — `AnonymousTest` / `AdminTest` base classes and shared interactions.
- `AuthTests.cs` — login, registration and authorization redirects (signed out).
- `DashboardTests.cs` — the home hero and quick-action tiles.
- `ParkingTests.cs` — leaderboard hero, price quote and a reserve round-trip.
- `AdminTests.cs` — lists, settings tabs, saving settings and the collusion empty state.
- `AdminDetailTests.cs` — user/role edit and the create-form validation.
- `AccountTests.cs` — the account hub, profile sections and sign-out.
- `ResponsiveTests.cs` — no horizontal overflow and the compact header at 390px.

Credentials come from `IdentitySeed` in `src/D3Parking.Web/appsettings.json`
(`admin@d3parking.local` / `Admin123$`).
